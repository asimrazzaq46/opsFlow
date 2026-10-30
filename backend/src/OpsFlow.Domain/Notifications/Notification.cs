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
}

/// <summary>
/// One persisted notification of one user in one tenant. Carries no message or note content - only the ticket reference and a short
/// subject - so it can never leak private text. <see cref="DedupKey"/> is unique per recipient, so retries cannot duplicate it.
/// </summary>
public sealed class Notification : ITenantOwned
{
    private Notification()
    {
        Subject = string.Empty;
        DedupKey = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public Guid TicketId { get; private set; }

    /// <summary>Display number and title of the ticket, e.g. "TKT-00042 Printer is broken".</summary>
    public string Subject { get; private set; }

    /// <summary>Optional short value such as the new status.</summary>
    public string? Detail { get; private set; }
    public string DedupKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }

    public static Notification Create(Guid tenantId, Guid userId, NotificationType type, Guid ticketId, string subject, string? detail, string dedupKey, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        Type = type,
        TicketId = ticketId,
        Subject = subject.Length > 200 ? subject[..200] : subject,
        Detail = detail is { Length: > 100 } ? detail[..100] : detail,
        DedupKey = dedupKey,
        CreatedAtUtc = nowUtc,
    };
}
