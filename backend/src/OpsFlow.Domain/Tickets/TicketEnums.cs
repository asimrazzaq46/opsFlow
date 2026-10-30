namespace OpsFlow.Domain.Tickets;

public enum TicketStatus
{
    Open,
    Assigned,
    InProgress,
    OnHold,
    Resolved,
    Closed,
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Urgent,
}

public enum TicketActivityType
{
    Created,
    DetailsUpdated,
    PriorityChanged,
    CategoryChanged,
    AgentAssigned,
    AgentUnassigned,
    StatusChanged,
    Resolved,
    Reopened,
    SlaCycleStarted,
    SlaCycleCompleted,
}

public enum SlaCycleReason
{
    Initial,
    Reopened,
}

public enum SlaState
{
    NotApplicable,
    Running,
    Overdue,
    Met,
    Missed,
}
