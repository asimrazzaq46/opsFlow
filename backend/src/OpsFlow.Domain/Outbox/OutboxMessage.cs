using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Outbox;

public enum OutboxStatus
{
    Pending,
    Claimed,
    RetryScheduled,
    Completed,
    DeadLetter,
}

/// <summary>Independent delivery destinations of one event; each is tracked durably so a retry never repeats a finished one.</summary>
[Flags]
public enum OutboxChannels
{
    None = 0,
    Realtime = 1,
    Email = 2,
    Cache = 4,
}

/// <summary>
/// Durable event written in the SAME transaction as the business change. Payloads carry ids only (never content or secrets); handlers
/// re-read current data and re-check authorization. <see cref="DedupKey"/> is unique per tenant so a retried business operation cannot enqueue twice.
/// </summary>
public sealed class OutboxMessage : ITenantOwned
{
    private OutboxMessage()
    {
        EventType = string.Empty;
        Payload = string.Empty;
        DedupKey = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string EventType { get; private set; }
    public int EventVersion { get; private set; }
    public Guid? AggregateId { get; private set; }
    public string Payload { get; private set; }
    public string DedupKey { get; private set; }
    public string? CorrelationId { get; private set; }
    public OutboxChannels RequiredChannels { get; private set; }
    public OutboxChannels CompletedChannels { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }

    /// <summary>Lease of the worker that claimed the row; an expired lease makes the row claimable again (crash recovery).</summary>
    public DateTime? ClaimedUntilUtc { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public string? LastError { get; private set; }

    public OutboxChannels PendingChannels => RequiredChannels & ~CompletedChannels;

    public static OutboxMessage Create(Guid tenantId, string eventType, int eventVersion, Guid? aggregateId, string payload, string dedupKey, OutboxChannels channels, string? correlationId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        EventType = eventType,
        EventVersion = eventVersion,
        AggregateId = aggregateId,
        Payload = payload,
        DedupKey = dedupKey.Length > 150 ? dedupKey[..150] : dedupKey,
        CorrelationId = correlationId,
        RequiredChannels = channels,
        Status = OutboxStatus.Pending,
        CreatedAtUtc = nowUtc,
        NextAttemptAtUtc = nowUtc,
    };
}
