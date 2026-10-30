namespace OpsFlow.Domain.Tickets;

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
    AttachmentAdded, // UPDATED: staff-only timeline entry (file name kabhi copy nahi hota)
}
