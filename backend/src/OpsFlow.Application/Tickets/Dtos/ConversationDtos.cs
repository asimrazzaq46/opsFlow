using OpsFlow.Domain.Notifications;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Tickets;

/// <summary>Customers never receive staff identities: for them a staff message has no sender id or name.</summary>
public sealed record TicketMessageDto(
    Guid Id,
    Guid TicketId,
    int Sequence,
    Guid? SenderUserId,
    string? SenderDisplayName,
    bool SenderIsStaff,
    string Content,
    DateTime CreatedAtUtc
);

public sealed record TicketNoteDto(
    Guid Id,
    Guid TicketId,
    int Sequence,
    Guid AuthorUserId,
    string AuthorDisplayName,
    string Content,
    DateTime CreatedAtUtc
);

public sealed record SendMessageCommand(string Content, Guid ClientMessageId);

public sealed record CreateNoteCommand(string Content, Guid ClientNoteId);

/// <summary>Positions the caller has received (delivered) and displayed (read); each must be the sequence of a stored chat message.</summary>
public sealed record AcknowledgeMessagesCommand(int? DeliveredSequence, int? ReadSequence);

/// <summary>The caller's own cursor only; other users' cursors are never returned.</summary>
public sealed record TicketConversationCursorDto(Guid TicketId, int DeliveredSequence, int ReadSequence);

public sealed record ChatUnreadSummaryDto(int Total);

/// <summary>Created = false when the same ClientMessageId/ClientNoteId was already stored (an idempotent retry returns the original).</summary>
public sealed record WriteResult<T>(T Value, bool Created);

public sealed record AttachmentDto(
    Guid Id,
    Guid TicketId,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool IsInternal,
    Guid? UploadedByUserId,
    DateTime CreatedAtUtc
);

public sealed record AttachmentDownload(Stream Content, string FileName, string ContentType);

public sealed record NotificationDto(
    Guid Id,
    Guid TenantId,
    NotificationType Type,
    Guid TicketId,
    string Subject,
    string? Detail,
    DateTime CreatedAtUtc,
    bool IsRead
);

public sealed record UnreadCountDto(int Count);

[RealtimeEvent("notification.created")]
public sealed record NotificationCreated(NotificationDto Notification);

[RealtimeEvent("ticket.message.created")]
public sealed record TicketMessageCreated(Guid TenantId, Guid TicketId, TicketMessageDto Message);

[RealtimeEvent("ticket.note.created")]
public sealed record TicketNoteCreated(Guid TenantId, Guid TicketId, TicketNoteDto Note);
