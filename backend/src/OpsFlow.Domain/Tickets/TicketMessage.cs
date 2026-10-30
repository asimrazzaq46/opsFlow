using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Customer-visible chat message. Immutable; sender, role flag, timestamp and sequence are server-assigned.</summary>
public sealed class TicketMessage : ITenantOwned
{
    private TicketMessage()
    {
        Content = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }

    /// <summary>Position in the ticket's conversation (unique per ticket).</summary>
    public int Sequence { get; private set; }
    public Guid SenderUserId { get; private set; }
    public bool SenderIsStaff { get; private set; }
    public string Content { get; private set; }

    /// <summary>Client-generated id that makes retries idempotent (unique per sender and ticket).</summary>
    public Guid ClientMessageId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TicketMessage Create(Guid tenantId, Guid ticketId, int sequence, Guid senderUserId, bool senderIsStaff, string content, Guid clientMessageId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TicketId = ticketId,
        Sequence = sequence,
        SenderUserId = senderUserId,
        SenderIsStaff = senderIsStaff,
        Content = content.Trim(),
        ClientMessageId = clientMessageId,
        CreatedAtUtc = nowUtc,
    };
}
