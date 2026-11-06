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
    SlaWarning, // raised by automated SLA monitoring
    SlaBreached,
    TicketClosed, // appended: the type is persisted as text, so existing rows are unaffected
}
