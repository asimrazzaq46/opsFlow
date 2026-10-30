using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Notifications;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    /// <summary>Share of the permitted duration after which an active, unsatisfied deadline triggers its single warning.</summary>
    public int WarningPercent { get; set; } = 75;

    /// <summary>Max cycles handled per kind and scan; the next scan continues (overdue work is never skipped, only deferred).</summary>
    public int BatchSize { get; set; } = 200;
}

public sealed record SlaScanResult(int Warnings, int Breaches);

/// <summary>
/// Recurring scanner over the EXISTING SLA cycles (no second SLA engine). It is restart-safe (it recomputes from persisted instants, so
/// downtime only delays work), multi-worker safe (each warning/breach is claimed by one atomic conditional UPDATE of a marker column inside
/// the same transaction that stages the notifications) and cycle-aware (markers live on the cycle; a reopened ticket has a new cycle, an old
/// cycle is finished and can never match again). A deadline is overdue only STRICTLY after it, like <see cref="SlaEvaluation"/>.
/// </summary>
public sealed partial class SlaMonitor(IOpsFlowDbContext db, ITenantContext tenantContext, NotificationService notifications, IOptions<SlaOptions> options, TimeProvider clock, ILogger<SlaMonitor> logger)
{
    private enum Kind
    {
        ResponseWarning,
        ResponseBreach,
        ResolutionWarning,
        ResolutionBreach,
    }

    private sealed record Candidate(Guid Id, Guid TenantId, Kind Kind);

    public async Task<SlaScanResult> ScanAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var remaining = 1 - (Math.Clamp(options.Value.WarningPercent, 1, 99) / 100.0);
        var batch = options.Value.BatchSize;
        List<Candidate> candidates = [];
        // Cross-tenant READ of cycle ids only (privileged, narrow): every write below runs inside the cycle's own tenant scope.
        candidates.AddRange(await FindAsync(ResponseBreach(now), Kind.ResponseBreach, batch, ct));
        candidates.AddRange(await FindAsync(ResolutionBreach(now), Kind.ResolutionBreach, batch, ct));
        candidates.AddRange(await FindAsync(ResponseWarning(now, remaining), Kind.ResponseWarning, batch, ct));
        candidates.AddRange(await FindAsync(ResolutionWarning(now, remaining), Kind.ResolutionWarning, batch, ct));

        int warnings = 0, breaches = 0, failed = 0;
        foreach (var group in candidates.GroupBy(c => c.TenantId))
        {
            using (tenantContext.Begin(group.Key))
            {
                foreach (var candidate in group)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (await RecordAsync(candidate, now, remaining, ct))
                        {
                            if (candidate.Kind is Kind.ResponseBreach or Kind.ResolutionBreach)
                            {
                                breaches++;
                            }
                            else
                            {
                                warnings++;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        db.ChangeTracker.Clear();
                        LogItemFailed(logger, ex, candidate.Id, candidate.Kind.ToString());
                    }
                }
            }
        }

        if (warnings + breaches > 0)
        {
            LogScanned(logger, warnings, breaches);
        }

        // surfaced as a failed job; the untouched items are simply found again by the next scan
        return failed > 0 ? throw new InvalidOperationException($"{failed} SLA item(s) failed; see the logs.") : new SlaScanResult(warnings, breaches);
    }

    private async Task<List<Candidate>> FindAsync(Expression<Func<TicketSlaCycle, bool>> predicate, Kind kind, int batch, CancellationToken ct) =>
        (await db.TicketSlaCycles.IgnoreQueryFilters().AsNoTracking().Where(predicate).OrderBy(c => c.StartedAtUtc).Select(c => new { c.Id, c.TenantId }).Take(batch).ToListAsync(ct))
        .ConvertAll(c => new Candidate(c.Id, c.TenantId, kind));

    private async Task<bool> RecordAsync(Candidate candidate, DateTime now, double remaining, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);
        // The predicate is re-evaluated by the UPDATE itself: obsolete candidates (resolved, answered, already marked) update 0 rows.
        var cycles = db.TicketSlaCycles.Where(c => c.Id == candidate.Id);
        var marked = candidate.Kind switch
        {
            Kind.ResponseBreach => await cycles.Where(ResponseBreach(now)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponseBreachedAtUtc, (DateTime?)now), ct),
            Kind.ResolutionBreach => await cycles.Where(ResolutionBreach(now)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResolutionBreachedAtUtc, (DateTime?)now), ct),
            Kind.ResponseWarning => await cycles.Where(ResponseWarning(now, remaining)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponseWarnedAtUtc, (DateTime?)now), ct),
            _ => await cycles.Where(ResolutionWarning(now, remaining)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResolutionWarnedAtUtc, (DateTime?)now), ct),
        };
        if (marked == 0)
        {
            return false;
        }

        var cycle = await db.TicketSlaCycles.AsNoTracking().SingleAsync(c => c.Id == candidate.Id, ct);
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == cycle.TicketId, ct);
        // Historical breaches (cycle already finished) are recorded but never announced: nobody can act on them any more.
        if (cycle.IsActive)
        {
            var breach = candidate.Kind is Kind.ResponseBreach or Kind.ResolutionBreach;
            var what = candidate.Kind is Kind.ResponseWarning or Kind.ResponseBreach ? "FirstResponse" : "Resolution";
            var recipients = await notifications.StageAsync(ticket, breach ? NotificationType.SlaBreached : NotificationType.SlaWarning, Guid.Empty, $"sla:{candidate.Kind}:{cycle.Id}", toStaff: true, toCustomer: false, detail: what, ct: ct);
            if (recipients == 0)
            {
                // UPDATED: marker/breach record sahi rehta hai, lekin kisi ko notify nahi hua - ye chup nahi rehna chahiye
                LogNoRecipients(logger, cycle.TenantId, cycle.Id, ticket.Id, candidate.Kind.ToString());
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static Expression<Func<TicketSlaCycle, bool>> ResponseBreach(DateTime now) => c =>
        c.ResponseDueAtUtc != null && c.ResponseBreachedAtUtc == null && c.ResponseDueAtUtc < now && (c.FirstResponseAtUtc == null || c.FirstResponseAtUtc > c.ResponseDueAtUtc);

    private static Expression<Func<TicketSlaCycle, bool>> ResolutionBreach(DateTime now) => c =>
        c.ResolutionBreachedAtUtc == null && c.ResolutionDueAtUtc < now && (c.ResolvedAtUtc == null || c.ResolvedAtUtc > c.ResolutionDueAtUtc);

    // warn when elapsed >= pct of the duration  <=>  due - now <= (1 - pct) * minutes; never once the deadline has passed
    private static Expression<Func<TicketSlaCycle, bool>> ResponseWarning(DateTime now, double remaining) => c =>
        c.ResponseDueAtUtc != null && c.ResponseWarnedAtUtc == null && c.ResponseBreachedAtUtc == null && c.FirstResponseAtUtc == null && c.ResolvedAtUtc == null
        && c.ResponseDueAtUtc >= now && c.ResponseDueAtUtc <= now.AddMinutes(c.ResponseMinutes * remaining);

    private static Expression<Func<TicketSlaCycle, bool>> ResolutionWarning(DateTime now, double remaining) => c =>
        c.ResolutionWarnedAtUtc == null && c.ResolutionBreachedAtUtc == null && c.ResolvedAtUtc == null
        && c.ResolutionDueAtUtc >= now && c.ResolutionDueAtUtc <= now.AddMinutes(c.ResolutionMinutes * remaining);

    [LoggerMessage(Level = LogLevel.Information, Message = "SLA scan recorded {Warnings} warning(s) and {Breaches} breach(es)")]
    private static partial void LogScanned(ILogger logger, int warnings, int breaches);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SLA {Kind} recorded for cycle {CycleId} (ticket {TicketId}, tenant {TenantId}) but no eligible recipient exists; nobody was notified")]
    private static partial void LogNoRecipients(ILogger logger, Guid tenantId, Guid cycleId, Guid ticketId, string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "SLA processing failed for cycle {CycleId} ({Kind})")]
    private static partial void LogItemFailed(ILogger logger, Exception exception, Guid cycleId, string kind);
}
