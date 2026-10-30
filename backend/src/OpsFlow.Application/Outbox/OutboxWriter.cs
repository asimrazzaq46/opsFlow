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
}

public sealed record NotificationEvent(Guid NotificationId);

public sealed record TicketMessageEvent(Guid TicketId, Guid MessageId);

public sealed record TicketNoteEvent(Guid TicketId, Guid NoteId);

public sealed record CacheInvalidatedEvent(string Resource);

/// <summary>
/// Stages events in the caller's DbContext. They are committed by the SAME SaveChanges as the business change, so a rollback removes them
/// and a commit makes them durable. Never call SaveChanges here.
/// </summary>
public sealed class OutboxWriter(IOpsFlowDbContext db, TimeProvider clock)
{
    /// <summary>Durable backstop for cache invalidation: the dispatcher repeats it if the immediate post-commit invalidation was lost (Redis down, crash).</summary>
    public void StageCacheInvalidation(Guid tenantId, string resource) =>
        Stage(tenantId, OutboxEvents.CacheInvalidated, null, new CacheInvalidatedEvent(resource), $"cache:{resource}:{Guid.NewGuid():N}", OutboxChannels.Cache);

    public void Stage(Guid tenantId, string eventType, Guid? aggregateId, object payload, string dedupKey, OutboxChannels channels) =>
        db.OutboxMessages.Add(OutboxMessage.Create(tenantId, eventType, 1, aggregateId, JsonSerializer.Serialize(payload), dedupKey, channels, Activity.Current?.TraceId.ToString(), clock.GetUtcNow().UtcDateTime));
}
