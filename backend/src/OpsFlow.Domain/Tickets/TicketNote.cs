using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Staff-only internal note. Kept in its own table so no customer-facing query can ever touch it. Immutable (history is preserved).</summary>
public sealed class TicketNote : ITenantOwned
{
    private TicketNote()
    {
        Content = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public int Sequence { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string Content { get; private set; }
    public Guid ClientNoteId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TicketNote Create(Guid tenantId, Guid ticketId, int sequence, Guid authorUserId, string content, Guid clientNoteId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TicketId = ticketId,
        Sequence = sequence,
        AuthorUserId = authorUserId,
        Content = content.Trim(),
        ClientNoteId = clientNoteId,
        CreatedAtUtc = nowUtc,
    };
}
