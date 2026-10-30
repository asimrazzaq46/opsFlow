using OpsFlow.Domain.Tickets;

namespace OpsFlow.Tests.Unit;

/// <summary>Exact-boundary behaviour of SLA cycles, the state machine and timeline ordering (pure domain, no database).</summary>
public class TicketDomainTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Ticket NewTicket(TicketPriority priority = TicketPriority.Medium) =>
        Ticket.Create(Tenant, 1, "Title", "Description", priority, Guid.NewGuid(), null, Guid.NewGuid(), T0);

    private static SlaPolicy Policy(int response = 60, int resolution = 240) => SlaPolicy.Create(Tenant, TicketPriority.Medium, response, resolution, T0);

    private static TicketSlaCycle Cycle(Ticket ticket, SlaCycleReason reason = SlaCycleReason.Initial, int response = 60, int resolution = 240) =>
        TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), reason, Policy(response, resolution), T0);

    [Fact]
    public void A_cycle_is_overdue_only_strictly_after_the_resolution_deadline()
    {
        var cycle = Cycle(NewTicket());
        var due = cycle.ResolutionDueAtUtc;

        Assert.Equal(T0.AddMinutes(240), due);
        Assert.Equal(SlaState.Running, SlaEvaluation.Resolution(cycle, due.AddTicks(-1)));
        Assert.Equal(SlaState.Running, SlaEvaluation.Resolution(cycle, due)); // exactly at the deadline is NOT overdue
        Assert.Equal(SlaState.Overdue, SlaEvaluation.Resolution(cycle, due.AddTicks(1)));
        Assert.Equal(240, SlaEvaluation.RemainingResolutionMinutes(cycle, T0));
    }

    [Fact]
    public void Resolving_exactly_at_the_deadline_meets_the_target_and_one_tick_later_misses_it()
    {
        var onTime = Cycle(NewTicket());
        var late = Cycle(NewTicket());

        onTime.Resolve(onTime.ResolutionDueAtUtc);
        late.Resolve(late.ResolutionDueAtUtc.AddTicks(1));

        Assert.Equal(SlaState.Met, SlaEvaluation.Resolution(onTime, T0.AddYears(1)));
        Assert.Equal(SlaState.Missed, SlaEvaluation.Resolution(late, T0.AddYears(1))); // a finished cycle never changes with time
        Assert.Null(SlaEvaluation.RemainingResolutionMinutes(onTime, T0));
        Assert.False(onTime.IsActive);
    }

    [Fact]
    public void Resolving_twice_keeps_the_first_completion_instant()
    {
        var cycle = Cycle(NewTicket());

        cycle.Resolve(T0.AddMinutes(10));
        cycle.Resolve(T0.AddMinutes(500));

        Assert.Equal(T0.AddMinutes(10), cycle.ResolvedAtUtc);
    }

    [Fact]
    public void Response_state_is_pending_until_a_real_first_response_exists()
    {
        var cycle = Cycle(NewTicket());

        Assert.Equal(SlaState.Running, SlaEvaluation.Response(cycle, cycle.ResponseDueAtUtc!.Value));
        Assert.Equal(SlaState.Overdue, SlaEvaluation.Response(cycle, cycle.ResponseDueAtUtc.Value.AddTicks(1)));
        Assert.Null(cycle.FirstResponseAtUtc); // never fabricated by ticket updates or resolution
    }

    [Fact]
    public void A_reopened_cycle_has_a_resolution_target_only()
    {
        var ticket = NewTicket();
        var first = Cycle(ticket);
        first.Resolve(T0.AddMinutes(30));
        var reopened = TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), SlaCycleReason.Reopened, Policy(), T0.AddHours(5));

        Assert.Equal(2, reopened.CycleNumber);
        Assert.Null(reopened.ResponseDueAtUtc);
        Assert.Equal(SlaState.NotApplicable, SlaEvaluation.Response(reopened, T0.AddDays(9)));
        Assert.Equal(T0.AddHours(5).AddMinutes(240), reopened.ResolutionDueAtUtc);
        Assert.Equal(T0.AddMinutes(30), first.ResolvedAtUtc); // the earlier cycle is untouched
    }

    [Fact]
    public void Reapplying_a_policy_changes_only_an_active_cycle_and_keeps_its_start()
    {
        var ticket = NewTicket();
        var active = Cycle(ticket);
        var finished = Cycle(ticket);
        finished.Resolve(T0.AddMinutes(5));
        var urgent = SlaPolicy.Create(Tenant, TicketPriority.Urgent, 10, 30, T0);

        active.ReapplyPolicy(urgent);
        finished.ReapplyPolicy(urgent);

        Assert.Equal(T0.AddMinutes(30), active.ResolutionDueAtUtc);
        Assert.Equal(T0.AddMinutes(10), active.ResponseDueAtUtc);
        Assert.Equal(T0.AddMinutes(240), finished.ResolutionDueAtUtc);
        Assert.Equal(240, finished.ResolutionMinutes);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 5)]
    [InlineData(10, 525_601)]
    public void Policies_reject_invalid_targets(int response, int resolution) =>
        Assert.Throws<ArgumentException>(() => SlaPolicy.Create(Tenant, TicketPriority.Low, response, resolution, T0));

    [Theory]
    [InlineData(TicketStatus.Assigned, TicketStatus.InProgress, true)]
    [InlineData(TicketStatus.Assigned, TicketStatus.Resolved, true)]
    [InlineData(TicketStatus.InProgress, TicketStatus.OnHold, true)]
    [InlineData(TicketStatus.OnHold, TicketStatus.InProgress, true)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Closed, true)]
    [InlineData(TicketStatus.Open, TicketStatus.Resolved, false)]
    [InlineData(TicketStatus.Open, TicketStatus.InProgress, false)]
    [InlineData(TicketStatus.Resolved, TicketStatus.InProgress, false)] // only reopen leaves Resolved
    [InlineData(TicketStatus.Closed, TicketStatus.Resolved, false)]
    [InlineData(TicketStatus.Assigned, TicketStatus.Closed, false)]
    [InlineData(TicketStatus.InProgress, TicketStatus.Assigned, false)]
    public void The_state_machine_allows_only_the_listed_transitions(TicketStatus from, TicketStatus to, bool allowed) =>
        Assert.Equal(allowed, TicketWorkflow.IsAllowed(from, to));

    [Fact]
    public void Only_active_work_statuses_require_an_assignee()
    {
        Assert.All([TicketStatus.Assigned, TicketStatus.InProgress, TicketStatus.OnHold], s => Assert.True(TicketWorkflow.RequiresAssignee(s)));
        Assert.All([TicketStatus.Open, TicketStatus.Resolved, TicketStatus.Closed], s => Assert.False(TicketWorkflow.RequiresAssignee(s)));
    }

    [Fact]
    public void Timeline_entries_get_consecutive_sequence_numbers_and_only_customer_events_are_visible()
    {
        var ticket = NewTicket();

        var created = ticket.Record(TicketActivityType.Created, Guid.NewGuid(), T0);
        var assigned = ticket.Record(TicketActivityType.AgentAssigned, Guid.NewGuid(), T0);
        var status = ticket.Record(TicketActivityType.StatusChanged, Guid.NewGuid(), T0, "Open", "Assigned");
        var sla = ticket.Record(TicketActivityType.SlaCycleStarted, null, T0);

        Assert.Equal([1, 2, 3, 4], new[] { created.Sequence, assigned.Sequence, status.Sequence, sla.Sequence });
        Assert.True(created.CustomerVisible);
        Assert.True(status.CustomerVisible);
        Assert.False(assigned.CustomerVisible); // internal staffing never reaches customers
        Assert.False(sla.CustomerVisible);
        Assert.Null(sla.ActorUserId); // system action
    }

    [Fact]
    public void Every_mutation_bumps_the_version_so_concurrent_writers_conflict()
    {
        var ticket = NewTicket();
        var v = ticket.Version;

        ticket.ChangePriority(TicketPriority.High, T0);
        ticket.SetStatus(TicketStatus.Assigned, T0);
        ticket.MarkResolved(T0);
        ticket.Reopen(TicketStatus.Assigned, T0);

        Assert.Equal(v + 4, ticket.Version);
        Assert.Null(ticket.ResolvedAtUtc);
    }

    [Fact]
    public void Ticket_numbers_are_formatted_with_the_tenant_prefix()
    {
        Assert.Equal("TKT-00042", TenantSettings.FormatNumber("TKT", 42));
        Assert.Equal("ACME-123456", TenantSettings.FormatNumber("ACME", 123_456));
    }
}
