namespace OpsFlow.Domain.Tickets;

/// <summary>
/// The ticket state machine. Open/Assigned are reached ONLY through assignments, and Resolved/Closed -> reopen has its own
/// operation (it starts a new SLA cycle), so the status endpoint can only move along the transitions listed here.
/// </summary>
public static class TicketWorkflow
{
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Transitions = new()
    {
        [TicketStatus.Assigned] = [TicketStatus.InProgress, TicketStatus.Resolved],
        [TicketStatus.InProgress] = [TicketStatus.OnHold, TicketStatus.Resolved],
        [TicketStatus.OnHold] = [TicketStatus.InProgress, TicketStatus.Resolved],
        [TicketStatus.Resolved] = [TicketStatus.Closed],
    };

    public static bool IsAllowed(TicketStatus from, TicketStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Statuses in which a ticket must have at least one active assignee.</summary>
    public static bool RequiresAssignee(TicketStatus status) =>
        status is TicketStatus.Assigned or TicketStatus.InProgress or TicketStatus.OnHold;

    public static bool IsFinished(TicketStatus status) =>
        status is TicketStatus.Resolved or TicketStatus.Closed;

    /// <summary>Whether customers may be shown this event type (internal staffing/SLA events never are).</summary>
    public static bool IsCustomerVisible(TicketActivityType type) =>
        type
            is TicketActivityType.Created
                or TicketActivityType.StatusChanged
                or TicketActivityType.Resolved
                or TicketActivityType.Reopened;
}
