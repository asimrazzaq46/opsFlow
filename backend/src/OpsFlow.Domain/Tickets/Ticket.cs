using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

public sealed class Ticket : ITenantOwned
{
    private Ticket()
    {
        Title = string.Empty;
        Description = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Tenant-scoped sequence number allocated atomically from TenantTicketCounter; never changes.</summary>
    public long Number { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public TicketPriority Priority { get; private set; }
    public Guid CategoryId { get; private set; }

    /// <summary>The customer contact this ticket is for; null for staff-only tickets (e.g. an agent's own ticket).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The authenticated user that created the ticket (the requester identity); immutable.</summary>
    public Guid CreatedByUserId { get; private set; }
    public TicketStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token: every mutation increments it, so simultaneous changes cannot silently overwrite each other.</summary>
    public int Version { get; private set; }
    public int ActivitySequence { get; private set; }
    public int CycleCount { get; private set; }

    public static Ticket Create(Guid tenantId, long number, string title, string description, TicketPriority priority, Guid categoryId, Guid? customerId, Guid createdByUserId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Number = number,
        Title = title.Trim(),
        Description = description.Trim(),
        Priority = priority,
        CategoryId = categoryId,
        CustomerId = customerId,
        CreatedByUserId = createdByUserId,
        Status = TicketStatus.Open,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
        Version = 1,
    };

    public void UpdateDetails(string title, string description, DateTime nowUtc)
    {
        Title = title.Trim();
        Description = description.Trim();
        Touch(nowUtc);
    }

    public void ChangePriority(TicketPriority priority, DateTime nowUtc)
    {
        Priority = priority;
        Touch(nowUtc);
    }

    public void ChangeCategory(Guid categoryId, DateTime nowUtc)
    {
        CategoryId = categoryId;
        Touch(nowUtc);
    }

    /// <summary>Plain status change; callers must have validated the transition (see <see cref="TicketWorkflow"/>).</summary>
    public void SetStatus(TicketStatus status, DateTime nowUtc)
    {
        Status = status;
        Touch(nowUtc);
    }

    public void MarkResolved(DateTime nowUtc)
    {
        Status = TicketStatus.Resolved;
        ResolvedAtUtc = nowUtc;
        Touch(nowUtc);
    }

    public void Reopen(TicketStatus status, DateTime nowUtc)
    {
        Status = status;
        ResolvedAtUtc = null;
        Touch(nowUtc);
    }

    public int NextCycleNumber() => ++CycleCount;

    /// <summary>Records a timeline entry; the per-ticket sequence gives a deterministic order.</summary>
    public TicketActivity Record(TicketActivityType type, Guid? actorUserId, DateTime nowUtc, string? oldValue = null, string? newValue = null, Guid? relatedId = null) =>
        TicketActivity.Create(TenantId, Id, ++ActivitySequence, type, actorUserId, nowUtc, oldValue, newValue, relatedId);

    public void Touch(DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        Version++;
    }
}
