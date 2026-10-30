using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Multi-agent assignments, the status state machine, SLA cycles (resolve/reopen) and their concurrency guarantees.</summary>
public class TicketWorkflowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(TicketWorld World, TicketJson Ticket)> AssignedTicketAsync(params Guid[] agents)
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        if (agents.Length > 0)
        {
            ticket = await w.AssignAsync(ticket.Id, agents);
        }

        return (w, ticket);
    }

    // ---------------------------------------------------------------- assignments

    [Fact]
    public async Task Several_agents_can_work_on_one_ticket_and_history_is_preserved()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        var first = await w.AssignAsync(ticket.Id, w.Agent1Membership);
        var both = await w.AssignAsync(ticket.Id, w.Agent2Membership); // adding does not replace
        var removed = await w.Manager.ReadAsync<TicketJson>(await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}")));
        var active = await w.Manager.ReadAsync<List<AssignmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}/assignments")));
        var history = await w.Manager.ReadAsync<List<AssignmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}/assignments?includeHistory=true")));

        Assert.Equal("Assigned", first.Status);
        Assert.Equal(2, both.Assignees!.Count);
        Assert.Equal(w.Agent2Membership, Assert.Single(removed.Assignees!).MembershipId);
        Assert.Equal(w.Agent2Membership, Assert.Single(active).MembershipId);
        Assert.Equal(2, history.Count);
        var gone = history.Single(h => h.MembershipId == w.Agent1Membership);
        Assert.False(gone.IsActive);
        Assert.Equal(w.ManagerMembership, gone.UnassignedByMembershipId); // who removed whom is kept
        Assert.Equal(["AgentAssigned", "AgentAssigned", "AgentUnassigned"], (await w.ActivityAsync(w.Manager, ticket.Id)).Select(a => a.Type).Where(t => t.StartsWith("Agent", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task Assignment_rules_reject_invalid_agents_duplicates_and_unauthorized_callers()
    {
        var (w, ticket) = await AssignedTicketAsync(); // unassigned
        var other = await TicketWorld.CreateAsync(factory);
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        string Path() => w.Url($"tickets/{ticket.Id}/assignments");

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(Path(), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode); // duplicate active assignment
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Path(), new { membershipIds = new[] { w.Agent2Membership } })).StatusCode); // suspended
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Path(), new { membershipIds = new[] { other.Agent1Membership } })).StatusCode); // other tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Path(), new { membershipIds = new[] { w.ManagerMembership } })).StatusCode); // not an Agent
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Path(), new { membershipIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PostAsync(Path(), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(Path(), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode); // agents cannot manage the team
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(Path())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Manager.DeleteAsync($"{Path()}/{w.Agent2Membership}")).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_agents_account_cannot_be_assigned_and_history_survives_suspension()
    {
        var (w, ticket) = await AssignedTicketAsync();
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();

        var detail = await w.GetTicketAsync(w.Manager, ticket.Id);
        var history = await w.Manager.ReadAsync<List<AssignmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}/assignments?includeHistory=true")));

        Assert.False(Assert.Single(detail.Assignees!).IsAgentActive); // flagged, not deleted
        Assert.Single(history);
        (await w.Agent2.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/assignments"), new { membershipIds = new[] { w.Agent2Membership } })).StatusCode);
    }

    [Fact]
    public async Task The_last_assignee_cannot_be_removed_while_the_workflow_requires_one()
    {
        var (w, ticket) = await AssignedTicketAsync();
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");

        var remove = await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"));

        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Single((await w.GetTicketAsync(w.Manager, ticket.Id)).Assignees!);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/assignments"), new { membershipIds = new[] { w.Agent1Membership } })).StatusCode);
    }

    [Fact]
    public async Task Finished_tickets_cannot_be_assigned_until_reopened()
    {
        var (w, ticket) = await AssignedTicketAsync();
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/assignments"), new { membershipIds = new[] { w.Agent2Membership } })).StatusCode);
    }

    // ---------------------------------------------------------------- state machine

    [Fact]
    public async Task The_full_flow_customer_to_resolution_to_reopen_works_end_to_end()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var created = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(created.Id, w.Agent1Membership, w.Agent2Membership);

        Assert.Equal("InProgress", (await w.MoveAsync(w.Agent1, created.Id, "InProgress")).Status);
        Assert.Equal("OnHold", (await w.MoveAsync(w.Agent2, created.Id, "OnHold")).Status);
        Assert.Equal("InProgress", (await w.MoveAsync(w.Agent1, created.Id, "InProgress")).Status);
        var resolved = await w.MoveAsync(w.Agent1, created.Id, "Resolved");
        var reopened = await w.Customer.ReadAsync<TicketJson>(await w.Customer.PostAsync(w.Url($"tickets/{created.Id}/reopen"), new { }));
        var final = await w.GetTicketAsync(w.Manager, created.Id);

        Assert.NotNull(resolved.Summary.ResolvedAtUtc);
        Assert.Equal("Assigned", reopened.Status); // assignees are kept
        Assert.Null(final.Summary.ResolvedAtUtc);
        Assert.Equal(2, final.CurrentSla!.CycleNumber);
        Assert.Equal(["Created", "SlaCycleStarted", "AgentAssigned", "AgentAssigned", "StatusChanged", "StatusChanged", "StatusChanged", "StatusChanged", "Resolved", "SlaCycleCompleted", "Reopened", "SlaCycleStarted"],
            (await w.ActivityAsync(w.Manager, created.Id)).Select(a => a.Type).ToArray());
    }

    [Fact]
    public async Task Invalid_and_unauthorized_transitions_are_rejected()
    {
        var (w, open) = await AssignedTicketAsync();
        var assigned = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(assigned.Id, w.Agent1Membership);
        string Status(Guid id) => w.Url($"tickets/{id}/status");

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(Status(open.Id), new { status = "Resolved" })).StatusCode); // Open cannot resolve
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Status(open.Id), new { status = "Assigned" })).StatusCode); // only via assignments
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PostAsync(Status(assigned.Id), new { status = "InProgress" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Agent1.PostAsync(Status(assigned.Id), new { status = "Closed" })).StatusCode); // not allowed from Assigned at all
        await w.MoveAsync(w.Agent1, assigned.Id, "Resolved");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(Status(assigned.Id), new { status = "Closed" })).StatusCode); // agents never close
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(Status(assigned.Id), new { status = "InProgress" })).StatusCode); // Resolved -> InProgress only via reopen
        Assert.Equal("Closed", (await w.MoveAsync(w.Manager, assigned.Id, "Closed")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(Status(assigned.Id), new { status = "Resolved" })).StatusCode); // Closed is final
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PutAsync(w.Url($"tickets/{assigned.Id}"), new { title = "Edit closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(Status(assigned.Id), new { status = "Nonsense" })).StatusCode);
    }

    [Fact]
    public async Task An_unassigned_agent_cannot_change_status_even_when_they_created_the_ticket_earlier()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var own = await w.CreateTicketAsync(w.Agent1, new { title = "Agent task", description = "d", categoryId = w.CategoryId });
        await w.AssignAsync(own.Id, w.Agent2Membership); // add a colleague...
        (await w.Manager.DeleteAsync(w.Url($"tickets/{own.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode(); // ...then remove the creator

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(w.Url($"tickets/{own.Id}/status"), new { status = "InProgress" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.GetAsync(w.Url($"tickets/{own.Id}"))).StatusCode); // can still read what they created
    }

    // ---------------------------------------------------------------- SLA

    [Fact]
    public async Task Resolving_finalizes_the_cycle_and_the_outcome_depends_on_the_deadline()
    {
        var (w, onTime) = await AssignedTicketAsync();
        await w.AssignAsync(onTime.Id, w.Agent1Membership);
        var late = await w.CreateTicketAsync(w.Manager, new { title = "Will be late", description = "d", categoryId = w.CategoryId, priority = "Urgent", assigneeMembershipIds = new[] { w.Agent1Membership } });

        await w.MoveAsync(w.Agent1, onTime.Id, "Resolved");
        await w.AdvanceAsync(TimeSpan.FromHours(9)); // past the 8 hour Urgent target
        await w.MoveAsync(w.Agent1, late.Id, "Resolved");

        var met = (await w.SlaAsync(onTime.Id)).Current!;
        var missed = (await w.SlaAsync(late.Id)).Current!;
        Assert.Equal("Met", met.ResolutionState);
        Assert.NotNull(met.ResolvedAtUtc);
        Assert.Null(met.RemainingResolutionMinutes);
        Assert.Equal("Missed", missed.ResolutionState);
        Assert.Null(missed.FirstResponseAtUtc);
        Assert.Equal("Overdue", missed.ResponseState); // no customer-visible reply exists, and the response target has long passed
        Assert.Single(await w.ActivityAsync(w.Manager, late.Id), a => a.Type == "SlaCycleCompleted" && a.NewValue == "Missed");
    }

    [Fact]
    public async Task Each_reopen_starts_a_new_cycle_and_earlier_cycles_stay_intact()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Flaky", description = "d", categoryId = w.CategoryId, priority = "High", assigneeMembershipIds = new[] { w.Agent1Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        var cycle1 = (await w.SlaAsync(ticket.Id)).Current!;

        await w.AdvanceAsync(TimeSpan.FromHours(1));
        var first = await w.Manager.ReadAsync<TicketJson>(await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { }));
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        await w.AdvanceAsync(TimeSpan.FromHours(1));
        await w.MoveAsync(w.Manager, ticket.Id, "Closed");
        var second = await w.Manager.ReadAsync<TicketJson>(await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { })); // reopen from Closed

        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal([1, 2, 3], sla.Cycles.Select(c => c.CycleNumber).ToArray());
        Assert.Equal(["Initial", "Reopened", "Reopened"], sla.Cycles.Select(c => c.Reason).ToArray());
        Assert.Equal(cycle1.ResolutionDueAtUtc, sla.Cycles[0].ResolutionDueAtUtc); // history untouched
        Assert.Equal(cycle1.ResolvedAtUtc, sla.Cycles[0].ResolvedAtUtc);
        Assert.Equal("Met", sla.Cycles[0].ResolutionState);
        Assert.NotNull(sla.Cycles[0].ResponseDueAtUtc);
        Assert.Null(sla.Cycles[1].ResponseDueAtUtc); // a reopen only carries a resolution target
        Assert.Equal(sla.Cycles[2].StartedAtUtc.AddMinutes(1440), sla.Cycles[2].ResolutionDueAtUtc);
        Assert.Null(sla.Cycles[2].ResolvedAtUtc);
        Assert.Equal(sla.Cycles[2].Id, sla.Current!.Id);
        Assert.Equal("Assigned", first.Status);
        Assert.Equal("Assigned", second.Status);
    }

    [Fact]
    public async Task Reopen_rules_per_role_and_state()
    {
        var (w, open) = await AssignedTicketAsync();
        var ticket = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        string Reopen(Guid id) => w.Url($"tickets/{id}/reopen");

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(Reopen(ticket.Id), new { })).StatusCode); // not finished yet
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        Assert.Equal(HttpStatusCode.NotFound, (await w.Agent2.PostAsync(Reopen(ticket.Id), new { })).StatusCode); // not visible to an unrelated agent
        await w.MoveAsync(w.Manager, ticket.Id, "Closed");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.PostAsync(Reopen(ticket.Id), new { })).StatusCode); // customers only reopen RESOLVED tickets
        Assert.Equal(HttpStatusCode.OK, (await w.Agent1.PostAsync(Reopen(ticket.Id), new { })).StatusCode); // assigned agent may
        Assert.Equal(HttpStatusCode.Conflict, (await w.Customer.PostAsync(Reopen(open.Id), new { })).StatusCode); // their own ticket is still open
    }

    [Fact]
    public async Task A_priority_change_reapplies_the_policy_to_the_active_cycle_only()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Escalate me", description = "d", categoryId = w.CategoryId, priority = "Low", assigneeMembershipIds = new[] { w.Agent1Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        var finished = (await w.SlaAsync(ticket.Id)).Current!;
        await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { });

        (await w.Manager.PutAsync(w.Url($"tickets/{ticket.Id}"), new { priority = "Urgent" })).EnsureSuccessStatusCode();

        var cycles = (await w.SlaAsync(ticket.Id)).Cycles;
        Assert.Equal(finished.ResolutionDueAtUtc, cycles[0].ResolutionDueAtUtc); // old cycle immutable
        Assert.Equal(480, cycles[1].ResolutionMinutes);
        Assert.Equal(cycles[1].StartedAtUtc.AddMinutes(480), cycles[1].ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Sla_policies_are_tenant_isolated_validated_and_never_rewrite_existing_cycles()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var ticket = await a.CreateTicketAsync(a.Customer);
        var medium = (await a.Owner.ReadAsync<List<SlaPolicyJson>>(await a.Owner.GetAsync(a.Url("sla-policies")))).Single(p => p.Priority == "Medium");

        (await a.Owner.PutAsync(a.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 10, resolutionMinutes = 20 })).EnsureSuccessStatusCode();

        var cycle = (await a.SlaAsync(ticket.Id)).Current!;
        Assert.Equal(2880, cycle.ResolutionMinutes); // snapshot, not the edited policy
        var fresh = await a.CreateTicketAsync(a.Manager);
        Assert.Equal(20, (await a.SlaAsync(fresh.Id)).Current!.ResolutionMinutes);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url("sla-policies"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PutAsync(b.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 1, resolutionMinutes = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Owner.PutAsync(a.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 100, resolutionMinutes = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Owner.PutAsync(a.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 0, resolutionMinutes = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Owner.PostAsync(a.Url("sla-policies"), new { priority = "Medium", responseMinutes = 5, resolutionMinutes = 9 })).StatusCode); // one active policy per priority
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Agent1.PutAsync(a.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 1, resolutionMinutes = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Customer.GetAsync(a.Url("sla-policies"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Customer.GetAsync(a.Url($"tickets/{ticket.Id}/sla"))).StatusCode);
    }

    // ---------------------------------------------------------------- categories

    [Fact]
    public async Task Categories_are_tenant_scoped_unique_and_inactive_ones_stay_valid_history()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var other = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        var created = await w.Manager.ReadAsync<CategoryJson>(await w.Manager.PostAsync(w.Url("ticket-categories"), new { name = "Hardware" }));
        (await w.Manager.PutAsync(w.Url($"ticket-categories/{w.CategoryId}/status"), new { isActive = false })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url("ticket-categories"), new { name = " hardware " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PutAsync(w.Url($"ticket-categories/{created.Id}"), new { name = "BILLING" })).StatusCode);
        Assert.True((await other.Owner.PostAsync(other.Url("ticket-categories"), new { name = "Hardware" })).IsSuccessStatusCode); // the same name in another tenant is fine
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(w.Url("ticket-categories"), new { name = "Nope" })).StatusCode);
        Assert.Equal("General", (await w.GetTicketAsync(w.Manager, ticket.Id)).Summary.CategoryName); // history keeps the deactivated category
        var visibleToCustomer = await w.Customer.ReadAsync<List<CategoryJson>>(await w.Customer.GetAsync(w.Url("ticket-categories")));
        Assert.DoesNotContain(visibleToCustomer, c => c.Id == w.CategoryId);
        Assert.Contains(await w.Manager.ReadAsync<List<CategoryJson>>(await w.Manager.GetAsync(w.Url("ticket-categories?includeInactive=true"))), c => c.Id == w.CategoryId && !c.IsActive);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, new { title = "On old category", description = "d", categoryId = w.CategoryId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PutAsync(w.Url($"tickets/{ticket.Id}"), new { categoryId = other.CategoryId })).StatusCode);
    }

    // ---------------------------------------------------------------- deterministic concurrency (SaveGate freezes ONE request right before it commits)

    private async Task<(T Service, OpsFlowDbContext Db, IServiceScope Scope)> ServiceAsync<T>(TicketWorld w, ApiClient who, Guid membershipId, TenantRole role) where T : notnull
    {
        var scope = w.ScopeAs(who, membershipId, role);
        await Task.CompletedTask;
        return (scope.ServiceProvider.GetRequiredService<T>(), scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope);
    }

    [Fact]
    public async Task Two_simultaneous_assignments_of_the_same_agent_produce_exactly_one_active_assignment()
    {
        var (w, ticket) = await AssignedTicketAsync();
        var (loser, loserDb, loserScope) = await ServiceAsync<TicketAssignmentService>(w, w.Manager, w.ManagerMembership, TenantRole.Manager);
        var (winner, _, winnerScope) = await ServiceAsync<TicketAssignmentService>(w, w.Owner, w.OwnerMembership, TenantRole.TenantOwner);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => { TicketWorld.SignInAs(loserScope.ServiceProvider, w.Manager); return loser.AssignAsync(ticket.Id, [w.Agent1Membership], null, CancellationToken.None); });
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            TicketWorld.SignInAs(winnerScope.ServiceProvider, w.Owner);
            await winner.AssignAsync(ticket.Id, [w.Agent1Membership], null, CancellationToken.None); // commits first
            factory.SaveGate.Resume();

            var failure = await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout));
            Assert.Equal(409, failure.StatusCode); // its retry re-checked and found the agent already assigned
        }

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(1, await db.TicketAssignments.IgnoreQueryFilters().CountAsync(a => a.TicketId == ticket.Id && a.UnassignedAtUtc == null));
        Assert.Equal(1, await db.TicketActivities.IgnoreQueryFilters().CountAsync(a => a.TicketId == ticket.Id && a.Type == TicketActivityType.AgentAssigned)); // the loser left no timeline entry
    }

    [Fact]
    public async Task Parallel_http_assignments_of_one_agent_never_create_duplicates()
    {
        var (w, ticket) = await AssignedTicketAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/assignments"), new { membershipIds = new[] { w.Agent1Membership } })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single((await w.GetTicketAsync(w.Manager, ticket.Id)).Assignees!);
    }

    [Fact]
    public async Task A_manager_demoted_mid_request_cannot_commit_an_assignment()
    {
        var (w, ticket) = await AssignedTicketAsync();
        var (assigner, assignerDb, assignerScope) = await ServiceAsync<TicketAssignmentService>(w, w.Manager, w.ManagerMembership, TenantRole.Manager);
        using (assignerScope)
        {
            factory.SaveGate.PauseNextSave(assignerDb);
            var assigning = Task.Run(() => { TicketWorld.SignInAs(assignerScope.ServiceProvider, w.Manager); return assigner.AssignAsync(ticket.Id, [w.Agent1Membership], null, CancellationToken.None); });
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            (await w.Owner.PutAsync(w.Url($"members/{w.ManagerMembership}/role"), new { role = "Agent" })).EnsureSuccessStatusCode(); // authorization is lost mid-request
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => assigning.WaitAsync(QueryGate.Timeout))).StatusCode);
        }

        Assert.Equal("Open", (await w.GetTicketAsync(w.Owner, ticket.Id)).Status);
        Assert.Empty((await w.GetTicketAsync(w.Owner, ticket.Id)).Assignees!);
    }

    [PostgresOnlyFact] // UPDATED: SQLite mein loser ki explicit transaction writer ko block karti hai; PostgreSQL row-level conflict hi asal scenario hai
    public async Task Two_simultaneous_reopens_create_exactly_one_new_cycle()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Reopen race", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        var (loser, loserDb, loserScope) = await ServiceAsync<TicketWorkflowService>(w, w.Manager, w.ManagerMembership, TenantRole.Manager);
        var (winner, _, winnerScope) = await ServiceAsync<TicketWorkflowService>(w, w.Owner, w.OwnerMembership, TenantRole.TenantOwner);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => { TicketWorld.SignInAs(loserScope.ServiceProvider, w.Manager); return loser.ReopenAsync(ticket.Id, null, CancellationToken.None); });
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            TicketWorld.SignInAs(winnerScope.ServiceProvider, w.Owner);
            await winner.ReopenAsync(ticket.Id, null, CancellationToken.None);
            factory.SaveGate.Resume();

            Assert.Equal(409, (await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout))).StatusCode);
        }

        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal(2, sla.Cycles.Count);
        Assert.Single(sla.Cycles, c => c.ResolvedAtUtc is null); // never two active cycles
        Assert.Equal(1, (await w.ActivityAsync(w.Manager, ticket.Id)).Count(a => a.Type == "Reopened"));
    }

    [Fact]
    public async Task Parallel_http_reopens_yield_one_success_and_one_active_cycle()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Parallel reopen", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(2, (await w.SlaAsync(ticket.Id)).Cycles.Count);
    }

    [PostgresOnlyFact] // UPDATED: SQLite mein loser ki explicit transaction writer ko block karti hai; PostgreSQL row-level conflict hi asal scenario hai
    public async Task Conflicting_simultaneous_transitions_never_overwrite_each_other()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Transition race", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership, w.Agent2Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");
        var (loser, loserDb, loserScope) = await ServiceAsync<TicketWorkflowService>(w, w.Agent2, w.Agent2Membership, TenantRole.Agent);
        var (winner, _, winnerScope) = await ServiceAsync<TicketWorkflowService>(w, w.Agent1, w.Agent1Membership, TenantRole.Agent);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => { TicketWorld.SignInAs(loserScope.ServiceProvider, w.Agent2); return loser.TransitionAsync(ticket.Id, TicketStatus.OnHold, null, CancellationToken.None); });
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            TicketWorld.SignInAs(winnerScope.ServiceProvider, w.Agent1);
            await winner.TransitionAsync(ticket.Id, TicketStatus.Resolved, null, CancellationToken.None);
            factory.SaveGate.Resume();

            Assert.Equal(409, (await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout))).StatusCode);
        }

        var after = await w.GetTicketAsync(w.Manager, ticket.Id);
        Assert.Equal("Resolved", after.Status); // the winner's state; the loser's OnHold never landed
        var types = (await w.ActivityAsync(w.Manager, ticket.Id)).Select(a => a.Type).ToArray();
        Assert.Equal(1, types.Count(t => t == "Resolved"));
        Assert.DoesNotContain(await w.ActivityAsync(w.Manager, ticket.Id), a => a.NewValue == "OnHold"); // the loser's change AND its timeline entry rolled back
    }

    [Fact]
    public async Task A_stale_version_is_rejected_for_status_changes_and_assignments()
    {
        var (w, ticket) = await AssignedTicketAsync();
        var stale = ticket.Version;
        await w.AssignAsync(ticket.Id, w.Agent1Membership);

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress", version = stale })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/assignments"), new { membershipIds = new[] { w.Agent2Membership }, version = stale })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress", version = (await w.GetTicketAsync(w.Manager, ticket.Id)).Version })).StatusCode);
    }

    // ---------------------------------------------------------------- audit fix: a priority always keeps its active SLA policy

    [Fact]
    public async Task The_only_active_sla_policy_of_a_priority_cannot_be_deactivated()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var policies = await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(w.Url("sla-policies")));
        var medium = policies.Single(p => p.Priority == "Medium");

        foreach (var policy in policies)
        {
            Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PutAsync(w.Url($"sla-policies/{policy.Id}/status"), new { isActive = false })).StatusCode);
        }

        Assert.All(await w.Owner.ReadAsync<List<SlaPolicyJson>>(await w.Owner.GetAsync(w.Url("sla-policies"))), p => Assert.True(p.IsActive));
        Assert.Equal(HttpStatusCode.NoContent, (await w.Manager.PutAsync(w.Url($"sla-policies/{medium.Id}/status"), new { isActive = true })).StatusCode); // re-activating an active policy is a no-op
        Assert.Equal(HttpStatusCode.OK, (await w.Manager.PutAsync(w.Url($"sla-policies/{medium.Id}"), new { responseMinutes = 30, resolutionMinutes = 90 })).StatusCode); // edit instead
        Assert.Equal("Medium", (await w.CreateTicketAsync(w.Manager)).Summary.Priority); // ticket creation for that priority still works
    }

    // ---------------------------------------------------------------- audit fix: ineligible (suspended / deactivated / re-roled) assigned agents

    [Fact]
    public async Task Ineligible_assignees_keep_their_history_but_no_longer_count_as_owners()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(ticket.Id, w.Agent1Membership, w.Agent2Membership);
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();

        var detail = await w.GetTicketAsync(w.Manager, ticket.Id);

        Assert.Equal(2, detail.Summary.AssigneeCount); // history/rows untouched
        Assert.Equal(1, detail.Summary.EligibleAssigneeCount);
        Assert.False(detail.Assignees!.Single(a => a.MembershipId == w.Agent1Membership).IsAgentActive);
        Assert.True(detail.Assignees!.Single(a => a.MembershipId == w.Agent2Membership).IsAgentActive);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress" })).StatusCode); // their mutations are blocked
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url("tickets"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent2Membership}"))).StatusCode); // last ELIGIBLE assignee stays
        (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode(); // the ineligible one can always be removed
        var history = await w.Manager.ReadAsync<List<AssignmentJson>>(await w.Manager.GetAsync(w.Url($"tickets/{ticket.Id}/assignments?includeHistory=true")));
        Assert.Equal(2, history.Count);
        Assert.False(history.Single(h => h.MembershipId == w.Agent1Membership).IsActive);
    }

    [Fact]
    public async Task A_ticket_whose_assignees_are_all_ineligible_is_flagged_and_must_be_reassigned()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Orphan soon", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "InProgress");
        var healthy = await w.CreateTicketAsync(w.Manager, new { title = "Healthy", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent2Membership } });
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();

        async Task<Guid[]> UnstaffedAsync() =>
            (await w.Manager.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Manager.GetAsync(w.Url("tickets?state=Unstaffed")))).Items.Select(t => t.Id).ToArray();

        Assert.Equal([ticket.Id], await UnstaffedAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "OnHold" })).StatusCode); // no work without an eligible owner
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "Resolved" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).StatusCode); // assign someone first
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url("tickets?state=Unstaffed"))).StatusCode);

        await w.AssignAsync(ticket.Id, w.Agent2Membership); // Manager/Owner reassignment
        (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();

        Assert.DoesNotContain(ticket.Id, await UnstaffedAsync());
        Assert.DoesNotContain(healthy.Id, await UnstaffedAsync());
        Assert.Equal("OnHold", (await w.MoveAsync(w.Agent2, ticket.Id, "OnHold")).Status);
    }

    [Fact]
    public async Task Deactivated_and_re_roled_agents_are_ineligible_and_reopen_falls_back_to_open()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Reopen me", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership, w.Agent2Membership } });
        await w.MoveAsync(w.Agent1, ticket.Id, "Resolved");
        (await w.Agent2.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).EnsureSuccessStatusCode(); // account deactivated
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/role"), new { role = "Manager" })).EnsureSuccessStatusCode(); // no longer an Agent

        var before = await w.GetTicketAsync(w.Manager, ticket.Id);
        var reopened = await w.Manager.ReadAsync<TicketJson>(await w.Manager.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { }));

        Assert.Equal(0, before.Summary.EligibleAssigneeCount);
        Assert.Equal("Open", reopened.Status); // nobody eligible to own it
        Assert.Equal(2, reopened.Summary.AssigneeCount);
        Assert.Contains(ticket.Id, (await w.Manager.ReadAsync<PagedDto<TicketSummaryJson>>(await w.Manager.GetAsync(w.Url("tickets?state=Unstaffed")))).Items.Select(t => t.Id));
        Assert.Equal("Assigned", (await w.AssignAsync(ticket.Id, await EligibleAgentAsync(w))).Status); // a fresh eligible agent makes it Assigned again
    }

    private static async Task<Guid> EligibleAgentAsync(TicketWorld w)
    {
        var extra = await ApiClient.AddMemberAsync(w.Factory, w.Owner, w.Org.Id, "Agent");
        return (await w.Owner.MembersAsync(w.Org.Id)).Single(m => m.UserId == extra.UserId).Id;
    }

    // ---------------------------------------------------------------- audit fix: authorization must hold at the COMMIT boundary (PostgreSQL row-lock choreography)

    [PostgresOnlyFact]
    public async Task A_status_change_cannot_commit_after_its_actor_was_suspended_meanwhile()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Race", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        var activityBefore = (await w.ActivityAsync(w.Manager, ticket.Id)).Count;
        var (scope, suspension) = await w.HoldUncommittedAsync(w.SuspendMembershipSql(w.Agent1Membership)); // the suspension is in flight, not yet committed
        using (scope)
        await using (suspension)
        {
            var inFlight = w.Agent1.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress" }); // passed authentication/authorization already
            await w.WaitForLockWaiterAsync(); // it is now blocked waiting for the actor's row
            await suspension.CommitAsync(); // the suspension wins

            Assert.Equal(HttpStatusCode.Forbidden, (await inFlight.WaitAsync(QueryGate.Timeout)).StatusCode);
        }

        Assert.Equal("Assigned", (await w.GetTicketAsync(w.Owner, ticket.Id)).Status);
        Assert.Equal(activityBefore, (await w.ActivityAsync(w.Owner, ticket.Id)).Count);
    }

    [PostgresOnlyFact]
    public async Task A_status_change_cannot_commit_after_its_actors_account_was_deactivated_meanwhile()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Race", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        var (scope, deactivation) = await w.HoldUncommittedAsync(w.DeactivateUserSql(w.Agent1.UserId));
        using (scope)
        await using (deactivation)
        {
            var inFlight = w.Agent1.PostAsync(w.Url($"tickets/{ticket.Id}/status"), new { status = "InProgress" });
            await w.WaitForLockWaiterAsync();
            await deactivation.CommitAsync();

            Assert.Equal(HttpStatusCode.Forbidden, (await inFlight.WaitAsync(QueryGate.Timeout)).StatusCode);
        }

        Assert.Equal("Assigned", (await w.GetTicketAsync(w.Owner, ticket.Id)).Status);
    }

    [PostgresOnlyFact]
    public async Task A_ticket_creation_by_a_manager_demoted_meanwhile_is_rejected_and_consumes_no_number()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var (scope, demotion) = await w.HoldUncommittedAsync(w.DemoteMembershipSql(w.ManagerMembership));
        using (scope)
        await using (demotion)
        {
            var inFlight = w.TryCreateTicketAsync(w.Manager, new { title = "Too late", description = "d", categoryId = w.CategoryId, customerId = w.CustomerContactId });
            await w.WaitForLockWaiterAsync();
            await demotion.CommitAsync();

            Assert.Equal(HttpStatusCode.Forbidden, (await inFlight.WaitAsync(QueryGate.Timeout)).StatusCode); // an Agent may not create for a customer
        }

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(0, await db.TicketCounters.IgnoreQueryFilters().Where(c => c.TenantId == w.Org.Id).Select(c => c.LastNumber).SingleAsync()); // counter rolled back
        Assert.Equal(0, await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == w.Org.Id));
    }

    [PostgresOnlyFact]
    public async Task A_ticket_cannot_be_created_for_an_agent_suspended_meanwhile()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var (scope, suspension) = await w.HoldUncommittedAsync(w.SuspendMembershipSql(w.Agent1Membership));
        using (scope)
        await using (suspension)
        {
            var inFlight = w.TryCreateTicketAsync(w.Manager, new { title = "Assign on create", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
            await w.WaitForLockWaiterAsync();
            await suspension.CommitAsync();

            Assert.Equal(HttpStatusCode.BadRequest, (await inFlight.WaitAsync(QueryGate.Timeout)).StatusCode); // eligibility re-checked after the lock
        }

        using var check = factory.Services.CreateScope();
        Assert.Equal(0, await check.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == w.Org.Id));
    }

    [PostgresOnlyFact]
    public async Task Customer_and_category_management_by_a_manager_demoted_meanwhile_is_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var (scope, demotion) = await w.HoldUncommittedAsync(w.DemoteMembershipSql(w.ManagerMembership));
        using (scope)
        await using (demotion)
        {
            var customer = w.Manager.PostAsync(w.Url("customers"), new { name = "Late", email = $"late-{Guid.NewGuid():N}@example.com" });
            var category = w.Manager.PostAsync(w.Url("ticket-categories"), new { name = "Late category" });
            await w.WaitForLockWaiterAsync();
            await demotion.CommitAsync();

            Assert.Equal(HttpStatusCode.Forbidden, (await customer.WaitAsync(QueryGate.Timeout)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await category.WaitAsync(QueryGate.Timeout)).StatusCode);
        }

        Assert.DoesNotContain(await w.Owner.ReadAsync<List<CategoryJson>>(await w.Owner.GetAsync(w.Url("ticket-categories"))), c => c.Name == "Late category");
        Assert.DoesNotContain((await w.Owner.ReadAsync<PagedDto<CustomerJson>>(await w.Owner.GetAsync(w.Url("customers?search=late")))).Items, c => c.Name == "Late");
    }

    [PostgresOnlyFact]
    public async Task A_suspension_waits_for_an_in_flight_mutation_and_both_commit_consistently()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "Order", description = "d", categoryId = w.CategoryId, assigneeMembershipIds = new[] { w.Agent1Membership } });
        var (service, serviceDb, serviceScope) = await ServiceAsync<TicketWorkflowService>(w, w.Agent1, w.Agent1Membership, TenantRole.Agent);
        using (serviceScope)
        {
            factory.SaveGate.PauseNextSave(serviceDb); // the agent's transition holds its authorization locks and stops right before committing
            var transition = Task.Run(() => { TicketWorld.SignInAs(serviceScope.ServiceProvider, w.Agent1); return service.TransitionAsync(ticket.Id, TicketStatus.InProgress, null, CancellationToken.None); });
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            var suspension = w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/status"), new { isActive = false });
            await w.WaitForLockWaiterAsync(); // the suspension must wait for the commit boundary
            factory.SaveGate.Resume();

            await Task.WhenAny(transition.WaitAsync(QueryGate.Timeout)); // the response read may already see the suspension; only the committed state matters
            Assert.Equal(HttpStatusCode.NoContent, (await suspension.WaitAsync(QueryGate.Timeout)).StatusCode);
        }

        Assert.Equal("InProgress", (await w.GetTicketAsync(w.Owner, ticket.Id)).Status); // the legitimately authorized change landed...
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url("tickets"))).StatusCode); // ...and the suspension took effect right after
    }
}
