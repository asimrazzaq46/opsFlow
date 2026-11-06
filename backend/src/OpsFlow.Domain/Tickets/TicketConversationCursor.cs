using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>
/// One user's position in a ticket's public conversation. Both sequences only move forward and always point at a stored chat message
/// (the per-ticket sequence also numbers internal notes, so not every value is a message). Reading implies delivery.
/// </summary>
public sealed class TicketConversationCursor : ITenantOwned
{
    private TicketConversationCursor() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid UserId { get; private set; }
    public int DeliveredSequence { get; private set; }
    public int ReadSequence { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static TicketConversationCursor Create(
        Guid tenantId,
        Guid ticketId,
        Guid userId,
        int deliveredSequence,
        int readSequence,
        DateTime nowUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketId = ticketId,
            UserId = userId,
            DeliveredSequence = Math.Max(deliveredSequence, readSequence),
            ReadSequence = readSequence,
            UpdatedAtUtc = nowUtc,
        };
}
