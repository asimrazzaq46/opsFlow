using System.Diagnostics;
using System.Text.Json;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Outbox;

namespace OpsFlow.Application.Outbox;

public static class OutboxEvents
{
    public const string NotificationCreated = "notification.created";
    public const string TicketMessageCreated = "ticket.message.created";
    public const string TicketNoteCreated = "ticket.note.created";
    public const string CacheInvalidated = "cache.invalidated";
    public const string TicketTaskChanged = "ticket.task.changed";
    public const string TicketChanged = "ticket.changed";
    public const string CustomerEligibilityChanged = "customer.eligibility.changed";
}

public sealed record NotificationEvent(Guid NotificationId);

public sealed record TicketMessageEvent(Guid TicketId, Guid MessageId);

public sealed record TicketNoteEvent(Guid TicketId, Guid NoteId);

public sealed record CacheInvalidatedEvent(string Resource);

/// <summary>Minimal internal change signal; clients reload the authoritative task list. <c>Change</c> is a <see cref="TicketTaskChanges"/> value.</summary>
public sealed record TicketTaskEvent(Guid TicketId, Guid? TaskId, string Change, int? Version);

/// <summary>Id-only change signal for the whole staff of a workspace; clients reload through their own authorized queries. <c>Change</c> is a <see cref="TicketChanges"/> value.</summary>
public sealed record TicketChangedEvent(Guid TicketId, string Change);

/// <summary>Staged with the affected portal user decided by the server; the delivered state is re-read from the database at publish time.</summary>
public sealed record CustomerEligibilityEvent(Guid CustomerId, Guid UserId);

/// <summary>
/// Stages events in the caller's DbContext. They are committed by the SAME SaveChanges as the business change, so a rollback removes them
/// and a commit makes them durable. Never call SaveChanges here.
/// </summary>
public sealed class OutboxWriter(IOpsFlowDbContext db, TimeProvider clock)
{
    /// <summary>Durable backstop for cache invalidation: the dispatcher repeats it if the immediate post-commit invalidation was lost (Redis down, crash).</summary>
    public void StageCacheInvalidation(Guid tenantId, string resource) =>
        Stage(
            tenantId,
            OutboxEvents.CacheInvalidated,
            null,
            new CacheInvalidatedEvent(resource),
            $"cache:{resource}:{Guid.NewGuid():N}",
            OutboxChannels.Cache
        );

    /// <summary>Lets every active staff member's ticket list and board refresh; carries no ticket content.</summary>
    public void StageTicketChanged(Guid tenantId, Guid ticketId, string change) =>
        Stage(
            tenantId,
            OutboxEvents.TicketChanged,
            ticketId,
            new TicketChangedEvent(ticketId, change),
            $"ticket:{ticketId}:{Guid.NewGuid():N}",
            OutboxChannels.Realtime
        );

    /// <summary>Tells one portal user that whether they may open new tickets changed; carries no customer data.</summary>
    public void StageCustomerEligibilityChanged(Guid tenantId, Guid customerId, Guid userId) =>
        Stage(
            tenantId,
            OutboxEvents.CustomerEligibilityChanged,
            customerId,
            new CustomerEligibilityEvent(customerId, userId),
            $"customer-eligibility:{customerId}:{Guid.NewGuid():N}",
            OutboxChannels.Realtime
        );

    public void Stage(
        Guid tenantId,
        string eventType,
        Guid? aggregateId,
        object payload,
        string dedupKey,
        OutboxChannels channels
    ) =>
        db.OutboxMessages.Add(
            OutboxMessage.Create(
                tenantId,
                eventType,
                1,
                aggregateId,
                JsonSerializer.Serialize(payload),
                dedupKey,
                channels,
                Activity.Current?.TraceId.ToString(),
                clock.GetUtcNow().UtcDateTime
            )
        );
}
