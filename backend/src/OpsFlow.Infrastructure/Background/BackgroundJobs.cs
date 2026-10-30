using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Outbox;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Outbox;

namespace OpsFlow.Infrastructure.Background;

/// <summary>Stable recurring-job ids: registration is an idempotent add-or-update, so restarts and several instances never duplicate a schedule.</summary>
public static class BackgroundJobIds
{
    public const string OutboxDispatch = "opsflow-outbox-dispatch";
    public const string SlaScan = "opsflow-sla-scan";
    public const string OutboxMaintenance = "opsflow-outbox-maintenance";
}

public sealed class BackgroundOptions
{
    public const string SectionName = "Background";

    /// <summary>Runs the Hangfire worker in THIS process. Off by default: tests, migrations and tooling never execute jobs.</summary>
    public bool ServerEnabled { get; set; }

    public int WorkerCount { get; set; } = 4;

    /// <summary>How often Hangfire looks for due recurring jobs (its own default is 15 s, too slow for a 10 s outbox schedule).</summary>
    public int SchedulePollingSeconds { get; set; } = 5;

    /// <summary>6-field cron (with seconds). Hangfire evaluates every schedule in UTC here.</summary>
    public string OutboxDispatchCron { get; set; } = "*/10 * * * * *";
    public string SlaScanCron { get; set; } = "0 * * * * *";
    public string OutboxMaintenanceCron { get; set; } = "0 15 * * * *";

    /// <summary>Upper bound of batches a single dispatch run drains before it yields to the next schedule.</summary>
    public int MaxDispatchBatches { get; set; } = 10;
}

// Recurring jobs re-run on their own schedule, so Hangfire's automatic retry would only pile up duplicates of a failing run.
public sealed class OutboxDispatchJob(
    OutboxDispatcher dispatcher,
    IOptions<OutboxOptions> outbox,
    IOptions<BackgroundOptions> background
)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < background.Value.MaxDispatchBatches; i++)
        {
            if (await dispatcher.DispatchAsync(cancellationToken) < outbox.Value.BatchSize)
            {
                return;
            }
        }
    }
}

public sealed class SlaScanJob(SlaMonitor monitor)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(CancellationToken cancellationToken) =>
        monitor.ScanAsync(cancellationToken);
}

/// <summary>Deletes old COMPLETED events (bounded) and reports the unfinished/dead-letter backlog. Dead letters are never deleted automatically.</summary>
public sealed partial class OutboxMaintenanceJob(
    IOpsFlowDbContext db,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxMaintenanceJob> logger
)
{
    private const int DeleteBatch = 1000;

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-options.Value.CompletedRetentionDays);
        int deleted;
        do
        {
            var ids = await db
                .OutboxMessages.Where(m =>
                    m.Status == OutboxStatus.Completed && m.ProcessedAtUtc < cutoff
                )
                .OrderBy(m => m.ProcessedAtUtc)
                .Select(m => m.Id)
                .Take(DeleteBatch)
                .ToListAsync(cancellationToken);
            deleted =
                ids.Count == 0
                    ? 0
                    : await db
                        .OutboxMessages.Where(m => ids.Contains(m.Id))
                        .ExecuteDeleteAsync(cancellationToken);
        } while (deleted == DeleteBatch);

        var pending = await db.OutboxMessages.CountAsync(
            m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Claimed,
            cancellationToken
        );
        var retrying = await db.OutboxMessages.CountAsync(
            m => m.Status == OutboxStatus.RetryScheduled,
            cancellationToken
        );
        var dead = await db.OutboxMessages.CountAsync(
            m => m.Status == OutboxStatus.DeadLetter,
            cancellationToken
        );
        if (dead > 0)
        {
            LogDeadLetters(logger, pending, retrying, dead);
        }
        else
        {
            LogStats(logger, pending, retrying);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox backlog: {Pending} pending, {Retrying} retrying, 0 dead letters"
    )]
    private static partial void LogStats(ILogger logger, int pending, int retrying);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox backlog: {Pending} pending, {Retrying} retrying, {Dead} DEAD LETTER(S) need operator attention"
    )]
    private static partial void LogDeadLetters(ILogger logger, int pending, int retrying, int dead);
}
