using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Append-only ticket timeline entry, persisted in the same transaction as the change it describes.</summary>
public sealed class TicketActivity : ITenantOwned
{
    private TicketActivity()
    {
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }

    /// <summary>Position in the ticket's timeline (unique per ticket).</summary>
    public int Sequence { get; private set; }
    public TicketActivityType Type { get; private set; }

    /// <summary>The acting user; null for system actions.</summary>
    public Guid? ActorUserId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public Guid? RelatedId { get; private set; }
    public bool CustomerVisible { get; private set; }

    internal static TicketActivity Create(Guid tenantId, Guid ticketId, int sequence, TicketActivityType type, Guid? actorUserId, DateTime nowUtc, string? oldValue, string? newValue, Guid? relatedId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TicketId = ticketId,
        Sequence = sequence,
        Type = type,
        ActorUserId = actorUserId,
        OccurredAtUtc = nowUtc,
        OldValue = oldValue is { Length: > 200 } ? oldValue[..200] : oldValue,
        NewValue = newValue is { Length: > 200 } ? newValue[..200] : newValue,
        RelatedId = relatedId,
        CustomerVisible = TicketWorkflow.IsCustomerVisible(type),
    };
}
