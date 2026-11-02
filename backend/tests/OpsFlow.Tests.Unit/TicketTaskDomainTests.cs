using OpsFlow.Domain.Tickets;

namespace OpsFlow.Tests.Unit;

/// <summary>TicketTask invariants: normalization, defaults, completion timestamp, versioning and that task activity stays internal.</summary>
public class TicketTaskDomainTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Manager = Guid.NewGuid();

    private static TicketTask NewTask(string title = "  Check logs  ", string? description = "  ") =>
        TicketTask.Create(Guid.NewGuid(), Guid.NewGuid(), title, description, Agent, 1, Manager, T0);

    [Fact]
    public void A_new_task_is_a_trimmed_todo_with_server_controlled_audit_fields()
    {
        var task = NewTask();

        Assert.Equal("Check logs", task.Title);
        Assert.Null(task.Description); // blank description is stored as none
        Assert.Equal((TicketTaskStatus.Todo, 1, 1), (task.Status, task.SortOrder, task.Version));
        Assert.Equal((Agent, Manager), (task.AssigneeMembershipId, task.CreatedByMembershipId));
        Assert.Null(task.UpdatedByMembershipId);
        Assert.Null(task.CompletedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_or_too_long_titles_and_descriptions_are_rejected(string title)
    {
        Assert.Throws<ArgumentException>(() => NewTask(title));
        Assert.Throws<ArgumentException>(() => NewTask(new string('x', TicketTask.TitleMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => NewTask("ok", new string('x', TicketTask.DescriptionMaxLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketTask.Create(Guid.NewGuid(), Guid.NewGuid(), "ok", null, Agent, 0, Manager, T0));
    }

    [Fact]
    public void Done_records_completion_and_leaving_done_clears_it()
    {
        var task = NewTask();

        task.ChangeStatus(TicketTaskStatus.Done, Agent, T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), task.CompletedAtUtc);
        task.ChangeStatus(TicketTaskStatus.InProgress, Agent, T0.AddHours(2));
        Assert.Null(task.CompletedAtUtc);
        task.ChangeStatus(TicketTaskStatus.Done, Agent, T0.AddHours(3));
        Assert.Equal(T0.AddHours(3), task.CompletedAtUtc);
        Assert.Equal((4, Agent, T0.AddHours(3)), (task.Version, task.UpdatedByMembershipId, task.UpdatedAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => task.ChangeStatus((TicketTaskStatus)42, Agent, T0));
    }

    [Fact]
    public void Every_mutation_bumps_the_concurrency_version_and_keeps_identity()
    {
        var task = NewTask();
        var (id, tenant, ticket) = (task.Id, task.TenantId, task.TicketId);
        var other = Guid.NewGuid();

        task.UpdateDetails("New", "Details", Agent, T0);
        task.Reassign(other, Manager, T0);
        task.MoveTo(3, Manager, T0);

        Assert.Equal(4, task.Version);
        Assert.Equal((other, 3, "Details"), (task.AssigneeMembershipId, task.SortOrder, task.Description));
        Assert.Equal((id, tenant, ticket), (task.Id, task.TenantId, task.TicketId));
    }

    [Theory]
    [InlineData(TicketActivityType.TaskCreated)]
    [InlineData(TicketActivityType.TaskDetailsUpdated)]
    [InlineData(TicketActivityType.TaskStatusChanged)]
    [InlineData(TicketActivityType.TaskAssigned)]
    [InlineData(TicketActivityType.TasksReordered)]
    public void Task_activity_is_never_customer_visible(TicketActivityType type) =>
        Assert.False(TicketWorkflow.IsCustomerVisible(type));
}
