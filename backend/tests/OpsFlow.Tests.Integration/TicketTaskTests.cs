using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public sealed record TaskJson(
    Guid Id, Guid TicketId, string Title, string? Description, string Status, int SortOrder, Guid AssigneeMembershipId, string AssigneeDisplayName,
    bool IsAssigneeEligible, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? CompletedAtUtc, int Version, bool CanEdit, bool CanChangeStatus);

public sealed record TaskSummaryJson(int TotalTasks, int TodoTasks, int InProgressTasks, int CompletedTasks);

public sealed record TaskListJson(List<TaskJson> Tasks, TaskSummaryJson Summary, bool IsReadOnly, bool CanCreate, bool CanManage);

/// <summary>Internal child tasks: permissions, ownership, concurrency, ordering, lifecycle, audit privacy and realtime audience.</summary>
public class TicketTaskTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Tasks(TicketWorld w, Guid ticketId, string tail = "") => w.Url($"tickets/{ticketId}/tasks{tail}");

    private static Task<HttpResponseMessage> TryCreateAsync(TicketWorld w, ApiClient who, Guid ticketId, string title = "Check logs", Guid? assignee = null, string? description = null) =>
        who.PostAsync(Tasks(w, ticketId), new { title, description, assigneeMembershipId = assignee });

    private static async Task<TaskJson> CreateAsync(TicketWorld w, ApiClient who, Guid ticketId, string title = "Check logs", Guid? assignee = null)
    {
        var response = await TryCreateAsync(w, who, ticketId, title, assignee);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return await who.ReadAsync<TaskJson>(response);
    }

    private static async Task<TaskListJson> ListAsync(TicketWorld w, ApiClient who, Guid ticketId) =>
        await who.ReadAsync<TaskListJson>(await who.GetAsync(Tasks(w, ticketId)));

    private static Task<HttpResponseMessage> StatusAsync(TicketWorld w, ApiClient who, TaskJson t, string status, int? version = null) =>
        who.PatchAsync(Tasks(w, t.TicketId, $"/{t.Id}/status"), new { status, version = version ?? t.Version });

    private async Task<(TicketWorld W, TicketJson Ticket)> AssignedAsync()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        return (w, ticket);
    }

    [Fact]
    public async Task Manager_creates_ordered_todo_tasks_without_touching_the_parent_ticket_or_its_sla()
    {
        var (w, t) = await AssignedAsync();
        var slaBefore = await w.SlaAsync(t.Id);

        var first = await CreateAsync(w, w.Manager, t.Id, "  Collect logs  ", w.Agent1Membership);
        var second = await CreateAsync(w, w.Manager, t.Id, "Replace toner", w.Agent1Membership);

        Assert.Equal("Collect logs", first.Title); // trimmed
        Assert.Equal(("Todo", 1, 2), (first.Status, first.SortOrder, second.SortOrder));
        Assert.Null(first.CompletedAtUtc);
        Assert.True(first.IsAssigneeEligible);
        var after = await w.GetTicketAsync(w.Manager, t.Id);
        Assert.Equal((t.Status, t.Version), (after.Status, after.Version)); // parent status/version untouched
        Assert.Equal(slaBefore.Cycles.Count, (await w.SlaAsync(t.Id)).Cycles.Count); // no new SLA cycle
        Assert.Single(after.Assignees!);

        var list = await ListAsync(w, w.Agent1, t.Id);
        Assert.Equal(["Collect logs", "Replace toner"], list.Tasks.Select(x => x.Title));
        Assert.Equal(new TaskSummaryJson(2, 2, 0, 0), list.Summary);
        Assert.False(list.CanManage);
        Assert.True(list.CanCreate);
        Assert.True((await ListAsync(w, w.Manager, t.Id)).CanManage);
    }

    [Fact]
    public async Task Agents_create_only_self_assigned_tasks_and_assignees_must_be_eligible_and_assigned()
    {
        var (w, t) = await AssignedAsync();

        var own = await CreateAsync(w, w.Agent1, t.Id); // assignee derived from the token
        Assert.Equal(w.Agent1Membership, own.AssigneeMembershipId);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, await TryCreateAsync(w, w.Agent1, t.Id, assignee: w.Agent1Membership));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await TryCreateAsync(w, w.Agent1, t.Id, assignee: w.Agent2Membership));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await TryCreateAsync(w, w.Agent2, t.Id)); // not assigned to the parent ticket
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await w.Agent2.GetAsync(Tasks(w, t.Id)));

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await TryCreateAsync(w, w.Manager, t.Id, assignee: w.Agent2Membership)); // active agent, not on the ticket
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, assignee: w.ManagerMembership)); // not an Agent
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, assignee: Guid.NewGuid()));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id)); // managers must choose
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, "   ", w.Agent1Membership));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, new string('x', 201), w.Agent1Membership));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, "ok", w.Agent1Membership, new string('x', 4001)));
        Assert.Equal(2, (await ListAsync(w, w.Manager, t.Id)).Summary.TotalTasks);
    }

    [Fact]
    public async Task Agents_change_only_their_own_tasks_and_never_assignment_or_order()
    {
        var (w, t) = await AssignedAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);
        var task = await CreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await w.Agent2.PatchAsync(Tasks(w, t.Id, $"/{task.Id}"), new { title = "hijack", version = task.Version }));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await StatusAsync(w, w.Agent2, task, "Done"));
        Assert.False((await ListAsync(w, w.Agent2, t.Id)).Tasks.Single().CanEdit);

        var edited = await w.Agent1.ReadAsync<TaskJson>(await w.Agent1.PatchAsync(Tasks(w, t.Id, $"/{task.Id}"), new { title = "Collect logs", description = "From the print server", version = task.Version }));
        Assert.Equal(("Collect logs", "From the print server", task.Version + 1), (edited.Title, edited.Description, edited.Version));
        var cleared = await w.Agent1.ReadAsync<TaskJson>(await w.Agent1.PatchAsync(Tasks(w, t.Id, $"/{task.Id}"), new { title = "Collect logs", description = (string?)null, version = edited.Version }));
        Assert.Null(cleared.Description); // explicit null clears

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await w.Agent1.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/assignee"), new { assigneeMembershipId = w.Agent2Membership, version = cleared.Version }));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await w.Agent1.PutAsync(Tasks(w, t.Id, "/order"), new { tasks = new[] { new { taskId = task.Id, version = cleared.Version } } }));

        var moved = await w.Manager.ReadAsync<TaskJson>(await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/assignee"), new { assigneeMembershipId = w.Agent2Membership, version = cleared.Version }));
        Assert.Equal(w.Agent2Membership, moved.AssigneeMembershipId);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await StatusAsync(w, w.Agent1, moved, "Done")); // no longer the owner
        await HttpAssert.StatusAsync(HttpStatusCode.OK, await StatusAsync(w, w.Agent2, moved, "InProgress"));

        var activity = await w.ActivityAsync(w.Manager, t.Id);
        var assigned = activity.Single(a => a.Type == "TaskAssigned");
        Assert.Equal((w.Agent1Membership.ToString(), w.Agent2Membership.ToString()), (assigned.OldValue, assigned.NewValue));
    }

    [Fact]
    public async Task Status_transitions_record_completion_and_never_resolve_the_parent()
    {
        var (w, t) = await AssignedAsync();
        var task = await CreateAsync(w, w.Agent1, t.Id);

        var done = await w.Agent1.ReadAsync<TaskJson>(await StatusAsync(w, w.Agent1, task, "Done"));
        Assert.NotNull(done.CompletedAtUtc);
        Assert.Equal(new TaskSummaryJson(1, 0, 0, 1), (await ListAsync(w, w.Manager, t.Id)).Summary);
        Assert.Equal("Assigned", (await w.GetTicketAsync(w.Manager, t.Id)).Status); // all tasks done != ticket resolved

        var reopened = await w.Agent1.ReadAsync<TaskJson>(await StatusAsync(w, w.Agent1, done, "InProgress"));
        Assert.Null(reopened.CompletedAtUtc);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await StatusAsync(w, w.Agent1, reopened, "Blocked"));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await w.Agent1.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/status"), new { status = "Done" })); // version required

        var history = (await w.ActivityAsync(w.Manager, t.Id)).Where(a => a.Type == "TaskStatusChanged").Select(a => (a.OldValue, a.NewValue));
        Assert.Equal([("Todo", "Done"), ("Done", "InProgress")], history);
    }

    [Fact]
    public async Task Stale_versions_are_rejected_with_409_on_every_mutation()
    {
        var (w, t) = await AssignedAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);
        var task = await CreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, await StatusAsync(w, w.Agent1, task, "InProgress")); // version is now 2

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{task.Id}"), new { title = "stale", version = 1 }));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await StatusAsync(w, w.Manager, task, "Done", 1));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/assignee"), new { assigneeMembershipId = w.Agent2Membership, version = 1 }));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await w.Manager.PutAsync(Tasks(w, t.Id, "/order"), new { tasks = new[] { new { taskId = task.Id, version = 1 } } }));
        Assert.Equal("InProgress", (await ListAsync(w, w.Manager, t.Id)).Tasks.Single().Status);
    }

    [Fact]
    public async Task Reorder_is_atomic_complete_and_rejects_foreign_duplicate_or_missing_tasks()
    {
        var (w, t) = await AssignedAsync();
        var a = await CreateAsync(w, w.Manager, t.Id, "A", w.Agent1Membership);
        var b = await CreateAsync(w, w.Manager, t.Id, "B", w.Agent1Membership);
        var c = await CreateAsync(w, w.Manager, t.Id, "C", w.Agent1Membership);
        var other = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        var foreign = await CreateAsync(w, w.Manager, other.Id, "X", w.Agent1Membership);

        var reordered = await w.Manager.ReadAsync<TaskListJson>(await w.Manager.PutAsync(Tasks(w, t.Id, "/order"),
            new { tasks = new[] { c, a, b }.Select(x => new { taskId = x.Id, version = x.Version }) }));
        Assert.Equal(["C", "A", "B"], reordered.Tasks.Select(x => x.Title));
        Assert.Equal([1, 2, 3], reordered.Tasks.Select(x => x.SortOrder));
        Assert.Contains(await w.ActivityAsync(w.Manager, t.Id), x => x.Type == "TasksReordered");

        var fresh = reordered.Tasks.ToDictionary(x => x.Title);
        object Item(string title) => new { taskId = fresh[title].Id, version = fresh[title].Version };
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await w.Manager.PutAsync(Tasks(w, t.Id, "/order"), new { tasks = new[] { Item("A"), Item("A"), Item("B"), Item("C") } }));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await w.Manager.PutAsync(Tasks(w, t.Id, "/order"),
            new { tasks = new[] { Item("A"), Item("B"), Item("C"), new { taskId = foreign.Id, version = foreign.Version } } }));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await w.Manager.PutAsync(Tasks(w, t.Id, "/order"), new { tasks = new[] { Item("A"), Item("B") } })); // a task is missing
        Assert.Equal(["C", "A", "B"], (await ListAsync(w, w.Manager, t.Id)).Tasks.Select(x => x.Title)); // nothing partially applied

        // a task addressed through ANOTHER ticket's route does not exist there
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await StatusAsync(w, w.Manager, foreign with { TicketId = t.Id }, "Done"));
    }

    [Fact]
    public async Task Customers_never_see_tasks_task_audit_or_task_progress()
    {
        var (w, t) = await AssignedAsync();
        await CreateAsync(w, w.Agent1, t.Id, "Secret internal step");

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await w.Customer.GetAsync(Tasks(w, t.Id)));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await TryCreateAsync(w, w.Customer, t.Id));
        Assert.DoesNotContain(await w.ActivityAsync(w.Customer, t.Id), a => a.Type.StartsWith("Task", StringComparison.Ordinal));
        Assert.Contains(await w.ActivityAsync(w.Manager, t.Id), a => a.Type == "TaskCreated");
        Assert.DoesNotContain(await (await w.Customer.GetAsync(w.Url($"tickets/{t.Id}"))).Content.ReadAsStringAsync(), "Secret internal step", StringComparison.Ordinal);

        var gql = await w.Customer.PostAsync(w.Url("graphql"), new { query = $"{{ ticketTaskProgress(ticketIds: [\"{t.Id}\"]) {{ totalTasks }} }}" });
        var body = await gql.Content.ReadAsStringAsync();
        Assert.Contains("FORBIDDEN", body, StringComparison.Ordinal);
        Assert.DoesNotContain("totalTasks\":1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kanban_progress_is_one_batch_scoped_to_what_the_caller_may_see()
    {
        var (w, t) = await AssignedAsync();
        var other = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent2Membership);
        var task = await CreateAsync(w, w.Agent1, t.Id);
        await StatusAsync(w, w.Agent1, task, "Done");
        await CreateAsync(w, w.Agent1, t.Id);
        await CreateAsync(w, w.Agent2, other.Id);

        async Task<Dictionary<Guid, (int, int)>> ProgressAsync(ApiClient who)
        {
            var json = JsonDocument.Parse(await (await who.PostAsync(w.Url("graphql"),
                new { query = $"{{ ticketTaskProgress(ticketIds: [\"{t.Id}\", \"{other.Id}\"]) {{ ticketId totalTasks completedTasks }} }}" })).Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("data").GetProperty("ticketTaskProgress").EnumerateArray()
                .ToDictionary(e => e.GetProperty("ticketId").GetGuid(), e => (e.GetProperty("totalTasks").GetInt32(), e.GetProperty("completedTasks").GetInt32()));
        }

        var manager = await ProgressAsync(w.Manager);
        Assert.Equal((2, 1), manager[t.Id]);
        Assert.Equal((1, 0), manager[other.Id]);
        Assert.Equal([t.Id], (await ProgressAsync(w.Agent1)).Keys); // the other ticket is not theirs
    }

    [Fact]
    public async Task Other_tenants_cannot_read_or_write_tasks()
    {
        var (a, t) = await AssignedAsync();
        var task = await CreateAsync(a, a.Agent1, t.Id);
        var b = await TicketWorld.CreateAsync(factory);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await b.Manager.GetAsync(Tasks(a, t.Id))); // not a member of tenant A
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await b.Manager.GetAsync(Tasks(b, t.Id))); // forged: A's ticket through B's tenant route
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await StatusAsync(b, b.Manager, task, "Done"));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(a, a.Manager, t.Id, assignee: b.Agent1Membership)); // B's agent in A
        Assert.Equal("Todo", (await ListAsync(a, a.Manager, t.Id)).Tasks.Single().Status);
    }

    [Fact]
    public async Task Tasks_are_read_only_while_resolved_and_keep_their_progress_after_reopen()
    {
        var (w, t) = await AssignedAsync();
        var task = await CreateAsync(w, w.Agent1, t.Id);
        task = await w.Agent1.ReadAsync<TaskJson>(await StatusAsync(w, w.Agent1, task, "Done"));
        await w.MoveAsync(w.Agent1, t.Id, "InProgress");
        await w.MoveAsync(w.Agent1, t.Id, "Resolved");

        var resolved = await ListAsync(w, w.Manager, t.Id);
        Assert.True(resolved.IsReadOnly);
        Assert.False(resolved.Tasks.Single().CanChangeStatus);
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await TryCreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await StatusAsync(w, w.Manager, task, "Todo"));

        await HttpAssert.StatusAsync(HttpStatusCode.OK, await w.Customer.PostAsync(w.Url($"tickets/{t.Id}/reopen"), new { }));
        var reopened = await ListAsync(w, w.Manager, t.Id);
        Assert.False(reopened.IsReadOnly);
        Assert.Equal("Done", reopened.Tasks.Single().Status); // progress is not reset
        Assert.Equal(task.CompletedAtUtc, reopened.Tasks.Single().CompletedAtUtc);
        Assert.Single(reopened.Tasks); // no new task set
        await HttpAssert.StatusAsync(HttpStatusCode.OK, await StatusAsync(w, w.Manager, reopened.Tasks.Single(), "Todo"));
    }

    [Fact]
    public async Task Unassigning_an_agent_with_open_tasks_requires_reassignment_first()
    {
        var (w, t) = await AssignedAsync();
        var current = await w.AssignAsync(t.Id, w.Agent2Membership);
        var open = await CreateAsync(w, w.Agent1, t.Id);
        var finished = await CreateAsync(w, w.Agent1, t.Id, "Done one");
        await StatusAsync(w, w.Agent1, finished, "Done");

        var blocked = await w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}?version={current.Version}"));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, blocked);
        Assert.Contains("TICKET_AGENT_HAS_OPEN_TASKS", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{open.Id}/assignee"), new { assigneeMembershipId = w.Agent2Membership, version = open.Version }));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, await w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}")));

        var list = await ListAsync(w, w.Manager, t.Id);
        Assert.False(list.Tasks.Single(x => x.Title == "Done one").IsAssigneeEligible); // completed task keeps its historical owner
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, await w.Agent1.GetAsync(Tasks(w, t.Id))); // access ends with the assignment
    }

    [Fact]
    public async Task Suspended_agent_loses_task_access_immediately_and_the_task_needs_reassignment()
    {
        var (w, t) = await AssignedAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);
        var task = await CreateAsync(w, w.Agent1, t.Id);
        // security revocation is never blocked by the agent's open tasks
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false }));

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await w.Agent1.GetAsync(Tasks(w, t.Id)));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, await StatusAsync(w, w.Agent1, task, "Done"));
        var orphan = (await ListAsync(w, w.Manager, t.Id)).Tasks.Single();
        Assert.False(orphan.IsAssigneeEligible);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await TryCreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership)); // suspended: not eligible
        var moved = await w.Manager.ReadAsync<TaskJson>(await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/assignee"), new { assigneeMembershipId = w.Agent2Membership, version = orphan.Version }));
        Assert.True(moved.IsAssigneeEligible);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, await w.Manager.PatchAsync(Tasks(w, t.Id, $"/{task.Id}/assignee"), new { assigneeMembershipId = w.Agent1Membership, version = moved.Version }));
    }

    [Fact]
    public async Task A_failed_save_persists_neither_the_task_nor_its_audit_nor_its_event()
    {
        var (w, t) = await AssignedAsync();
        int Count(Func<IOpsFlowDbContext, int> f)
        {
            using var scope = w.ScopeAs(w.Owner, w.OwnerMembership, TenantRole.TenantOwner);
            return f(scope.ServiceProvider.GetRequiredService<IOpsFlowDbContext>());
        }

        var before = (Count(d => d.TicketActivities.Count()), Count(d => d.OutboxMessages.Count(o => o.EventType == "ticket.task.changed")));
        factory.Faults.FailNextSave();
        await HttpAssert.StatusAsync(HttpStatusCode.InternalServerError, await TryCreateAsync(w, w.Agent1, t.Id));

        Assert.Equal(0, Count(d => d.TicketTasks.Count()));
        Assert.Equal(before, (Count(d => d.TicketActivities.Count()), Count(d => d.OutboxMessages.Count(o => o.EventType == "ticket.task.changed"))));
        await CreateAsync(w, w.Agent1, t.Id);
        Assert.Equal(before.Item2 + 1, Count(d => d.OutboxMessages.Count(o => o.EventType == "ticket.task.changed")));
    }

    [Fact]
    public async Task Task_events_reach_only_managers_and_assigned_agents_without_content()
    {
        var (w, t) = await AssignedAsync();
        var outsider = await TicketWorld.CreateAsync(factory);
        await using var manager = HubClient.Create(factory, w.Manager.AccessToken);
        await using var agent1 = HubClient.Create(factory, w.Agent1.AccessToken);
        await using var agent2 = HubClient.Create(factory, w.Agent2.AccessToken);
        await using var customer = HubClient.Create(factory, w.Customer.AccessToken);
        await using var otherTenant = HubClient.Create(factory, outsider.Manager.AccessToken);
        foreach (var hub in new[] { manager, agent1, agent2, customer, otherTenant })
        {
            await hub.StartAsync();
        }

        var task = await CreateAsync(w, w.Agent1, t.Id, "Very private title");
        bool IsTask((string Name, JsonElement Payload) e) => e.Name == "ticket.task.changed" && e.Payload.GetProperty("taskId").GetGuid() == task.Id;

        Assert.True(await manager.WaitForAsync(IsTask));
        Assert.True(await agent1.WaitForAsync(IsTask));
        var payload = manager.Events.First(IsTask).Payload;
        Assert.Equal(("TaskCreated", t.Id), (payload.GetProperty("change").GetString(), payload.GetProperty("ticketId").GetGuid()));
        Assert.DoesNotContain("Very private title", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(agent2.Events, e => e.Name == "ticket.task.changed");
        Assert.DoesNotContain(customer.Events, e => e.Name == "ticket.task.changed");
        Assert.DoesNotContain(otherTenant.Events, e => e.Name == "ticket.task.changed");
    }

    [Fact]
    public async Task Revoked_agents_are_not_task_event_recipients()
    {
        var (w, t) = await AssignedAsync();
        using var scope = w.ScopeAs(w.Manager, w.ManagerMembership, TenantRole.Manager);
        var audience = scope.ServiceProvider.GetRequiredService<TicketAudience>();
        Assert.Contains(w.Agent1.UserId, await audience.TaskStaffAsync(t.Id, CancellationToken.None));
        Assert.DoesNotContain(w.Customer.UserId, await audience.TaskStaffAsync(t.Id, CancellationToken.None));

        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        var after = await audience.TaskStaffAsync(t.Id, CancellationToken.None);
        Assert.DoesNotContain(w.Agent1.UserId, after);
        Assert.Contains(w.Manager.UserId, after);
    }

    [PostgresOnlyFact]
    public async Task Task_reads_and_writes_use_a_small_constant_number_of_queries()
    {
        var (w, t) = await AssignedAsync();
        await CreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership);
        TaskJson created = null!;
        var create = await factory.Queries.MeasureAsync(async () => created = await CreateAsync(w, w.Manager, t.Id, assignee: w.Agent1Membership));
        var status = await factory.Queries.MeasureAsync(async () => await HttpAssert.StatusAsync(HttpStatusCode.OK, await StatusAsync(w, w.Agent1, created, "Done")));
        var list = await factory.Queries.MeasureAsync(async () => await ListAsync(w, w.Agent1, t.Id));
        // Includes the request pipeline's authentication/membership checks; PostgreSQL only, because command counts are provider-specific.
        Assert.True(create <= 11 && status <= 10 && list <= 5, $"create={create} status={status} list={list}");
    }

    [Fact]
    public async Task Unassigning_from_a_resolved_ticket_is_blocked_too_so_a_reopen_never_finds_orphaned_tasks()
    {
        var (w, t) = await AssignedAsync();
        await w.AssignAsync(t.Id, w.Agent2Membership);
        await CreateAsync(w, w.Agent1, t.Id);
        await w.MoveAsync(w.Agent2, t.Id, "InProgress");
        var resolved = await w.MoveAsync(w.Agent2, t.Id, "Resolved");

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}?version={resolved.Version}")));
        Assert.Contains((await w.GetTicketAsync(w.Manager, t.Id)).Assignees!, a => a.MembershipId == w.Agent1Membership);
    }

    /// <summary>Task creation holds the parent ticket row lock: an unassignment that races it waits and then sees the new open task.</summary>
    [PostgresOnlyFact]
    public async Task Concurrent_task_creation_and_agent_unassignment_cannot_both_succeed()
    {
        var (w, t) = await AssignedAsync();
        var current = await w.AssignAsync(t.Id, w.Agent2Membership);
        using var scope = w.ScopeAs(w.Manager, w.ManagerMembership, TenantRole.Manager);
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        factory.SaveGate.PauseNextSave(db);
        var creating = Task.Run(async () =>
        {
            TicketWorld.SignInAs(scope.ServiceProvider, w.Manager);
            return await scope.ServiceProvider.GetRequiredService<TicketTaskService>()
                .CreateAsync(t.Id, new CreateTicketTaskCommand("Race", null, w.Agent1Membership), CancellationToken.None);
        });
        Assert.True(await factory.SaveGate.WaitUntilPausedAsync()); // ticket row is locked, task not yet saved

        var unassigning = w.Manager.DeleteAsync(w.Url($"tickets/{t.Id}/assignments/{w.Agent1Membership}?version={current.Version}"));
        await w.WaitForLockWaiterAsync();
        factory.SaveGate.Resume();

        await creating.WaitAsync(QueryGate.Timeout);
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, await unassigning.WaitAsync(QueryGate.Timeout));
        Assert.Contains((await w.GetTicketAsync(w.Manager, t.Id)).Assignees!, a => a.MembershipId == w.Agent1Membership);
    }

    /// <summary>Two managers reorder from the same snapshot: exactly one wins, the other gets 409 and nothing is half-applied.</summary>
    [PostgresOnlyFact]
    public async Task Concurrent_reorders_from_the_same_snapshot_let_exactly_one_win()
    {
        var (w, t) = await AssignedAsync();
        var a = await CreateAsync(w, w.Manager, t.Id, "A", w.Agent1Membership);
        var b = await CreateAsync(w, w.Manager, t.Id, "B", w.Agent1Membership);
        object Body(params TaskJson[] order) => new { tasks = order.Select(x => new { taskId = x.Id, version = x.Version }) };

        var results = await Task.WhenAll(w.Manager.PutAsync(Tasks(w, t.Id, "/order"), Body(b, a)), w.Owner.PutAsync(Tasks(w, t.Id, "/order"), Body(b, a)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        Assert.Equal([1, 2], (await ListAsync(w, w.Manager, t.Id)).Tasks.Select(x => x.SortOrder));
    }
}
