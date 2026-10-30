using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Outbox;

namespace OpsFlow.Application.Outbox;

/// <summary>One delivery destination of outbox events. Must be idempotent: a lease can expire while a handler still runs.</summary>
public interface IOutboxChannelHandler
{
    OutboxChannels Channel { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// Claims due events with an atomic conditional UPDATE (safe for any number of workers/instances; no long transaction is held while
/// SMTP/SignalR run), runs every pending channel inside the event's OWN tenant scope and records each finished channel durably so a retry
/// only repeats the failed ones. Delivery is at-least-once: SMTP/SignalR can still deliver twice if a worker dies between the send and
/// the acknowledgement.
/// </summary>
public sealed partial class OutboxDispatcher(IOpsFlowDbContext db, ITenantContext tenantContext, IEnumerable<IOutboxChannelHandler> handlers, IOptions<OutboxOptions> options, TimeProvider clock, ILogger<OutboxDispatcher> logger)
{
    private readonly Dictionary<OutboxChannels, IOutboxChannelHandler> _handlers = handlers.ToDictionary(h => h.Channel);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Processes at most one bounded batch; returns the number of events this worker claimed.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = Now;
        var due = await db.OutboxMessages.AsNoTracking()
            .Where(m => ((m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.RetryScheduled) && m.NextAttemptAtUtc <= now) || (m.Status == OutboxStatus.Claimed && m.ClaimedUntilUtc < now))
            .OrderBy(m => m.NextAttemptAtUtc).Select(m => m.Id).Take(o.BatchSize).ToListAsync(ct);

        var claimedCount = 0;
        foreach (var id in due)
        {
            ct.ThrowIfCancellationRequested();
            var token = Guid.NewGuid();
            // UPDATED: lease/claim ka waqt har claim par taaza (batch-start ka purana `now` nahi): pehle ke slow events ke baad bhi lease poori milti hai
            var claimTime = Now;
            var lease = claimTime.AddSeconds(o.LeaseSeconds);
            var claimed = await db.OutboxMessages
                .Where(m => m.Id == id && (((m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.RetryScheduled) && m.NextAttemptAtUtc <= claimTime) || (m.Status == OutboxStatus.Claimed && m.ClaimedUntilUtc < claimTime)))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.Claimed).SetProperty(m => m.ClaimToken, (Guid?)token)
                    .SetProperty(m => m.ClaimedUntilUtc, (DateTime?)lease).SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1), ct);
            if (claimed == 0)
            {
                continue; // another worker won the claim
            }

            claimedCount++;
            await ProcessAsync(id, token, ct);
        }

        return claimedCount;
    }

    private async Task ProcessAsync(Guid id, Guid token, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, ct);
        Exception? failure = null;
        var permanent = false;
        using (tenantContext.Begin(message.TenantId)) // trusted tenant: taken from the persisted row, never from a request
        {
            foreach (var channel in Enum.GetValues<OutboxChannels>().Where(c => c != OutboxChannels.None && message.PendingChannels.HasFlag(c)))
            {
                try
                {
                    await (_handlers.TryGetValue(channel, out var handler) ? handler : throw new OutboxPermanentException($"No handler for channel {channel}.")).HandleAsync(message, ct);
                    // UPDATED: channel complete hone par lease bhi renew hoti hai (sirf claim-token ke owner ke liye): ek event ke kai channels ho to lease beech mein expire nahi hoti
                    var renewed = Now.AddSeconds(options.Value.LeaseSeconds);
                    await db.OutboxMessages.Where(m => m.Id == id && m.ClaimToken == token)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.CompletedChannels, m => m.CompletedChannels | channel).SetProperty(m => m.ClaimedUntilUtc, (DateTime?)renewed), CancellationToken.None);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // shutdown: the lease expires and another worker resumes the remaining channels
                }
                catch (OutboxPermanentException ex)
                {
                    (failure, permanent) = (ex, true);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    LogChannelFailed(logger, message.Id, message.EventType, channel, ex.GetType().Name);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        await FinishAsync(message, token, failure, permanent);
    }

    // Every update is guarded by the claim token: a worker whose lease expired can never overwrite the new owner's outcome.
    private async Task FinishAsync(OutboxMessage message, Guid token, Exception? failure, bool permanent)
    {
        var o = options.Value;
        var now = Now;
        var guarded = db.OutboxMessages.Where(m => m.Id == message.Id && m.ClaimToken == token);
        if (failure is null)
        {
            await guarded.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.Completed).SetProperty(m => m.ProcessedAtUtc, (DateTime?)now)
                .SetProperty(m => m.ClaimedUntilUtc, (DateTime?)null).SetProperty(m => m.LastError, (string?)null), CancellationToken.None);
            return;
        }

        var error = Summarize(failure);
        if (permanent || message.AttemptCount >= o.MaxAttempts)
        {
            await guarded.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.DeadLetter).SetProperty(m => m.ProcessedAtUtc, (DateTime?)now)
                .SetProperty(m => m.ClaimedUntilUtc, (DateTime?)null).SetProperty(m => m.LastError, error), CancellationToken.None);
            LogDeadLetter(logger, message.Id, message.EventType, message.AttemptCount);
            return;
        }

        var next = now.AddSeconds(Backoff(message.AttemptCount, o));
        await guarded.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.RetryScheduled).SetProperty(m => m.NextAttemptAtUtc, next)
            .SetProperty(m => m.ClaimedUntilUtc, (DateTime?)null).SetProperty(m => m.LastError, error), CancellationToken.None);
    }

    /// <summary>Exponential backoff capped at MaxDelaySeconds with +-20% jitter so retries of many events do not synchronize.</summary>
    public static double Backoff(int attempt, OutboxOptions o) =>
        Math.Min(o.MaxDelaySeconds, o.BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1))) * (0.8 + (Random.Shared.NextDouble() * 0.4));

    private static string Summarize(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length > 500 ? text[..500] : text;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox event {EventId} ({EventType}) failed on channel {Channel}: {ErrorType}")]
    private static partial void LogChannelFailed(ILogger logger, Guid eventId, string eventType, OutboxChannels channel, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox event {EventId} ({EventType}) moved to the dead letters after {Attempts} attempt(s)")]
    private static partial void LogDeadLetter(ILogger logger, Guid eventId, string eventType, int attempts);
}
