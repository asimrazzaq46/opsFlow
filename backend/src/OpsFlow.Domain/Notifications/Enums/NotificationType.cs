using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Notifications;

public enum NotificationType
{
    TicketCreated,
    TicketAssigned,
    TicketUnassigned,
    TicketStatusChanged,
    TicketResolved,
    TicketReopened,
    MessageReceived,
    NoteAdded,
    SlaWarning, // UPDATED: Milestone 4 - automated SLA monitoring
    SlaBreached,
}
