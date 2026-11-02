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
    AttachmentAdded, // staff-only timeline entry (the file name is never copied)

    // Child task audit; not in the IsCustomerVisible whitelist, so always internal.
    TaskCreated,
    TaskDetailsUpdated,
    TaskStatusChanged,
    TaskAssigned,
    TasksReordered,
}
