using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Analytics;

/// <summary>
/// Dashboard and report aggregates computed from persisted tickets, SLA cycles and timeline entries (no duplicated counters).
/// Scope is ALWAYS <see cref="TicketAccess.Visible"/> (tenant filter + role visibility) applied first, so an Agent only ever aggregates
/// tickets they may see and a Customer only their own. Every figure is aggregated in the database (counts, averages, median, buckets);
/// nothing is capped or sampled. Metric definitions: docs/analytics-graphql.md.
/// </summary>
public sealed class AnalyticsService(IOpsFlowDbContext db, TicketAccess access, IReportingTimeZoneProvider zones, TimeProvider clock)
{
    private const int MaxExportRows = 10_000;

    // The single definition of each duration; PostgreSQL translates it to interval arithmetic, SQLite (tests) evaluates the same expression in memory.
    private static readonly Expression<Func<TicketSlaCycle, double>> ResolutionMinutes = c => (c.ResolvedAtUtc!.Value - c.StartedAtUtc).TotalMinutes;
    private static readonly Expression<Func<TicketSlaCycle, double>> FirstResponseMinutes = c => (c.FirstResponseAtUtc!.Value - c.StartedAtUtc).TotalMinutes;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Inclusive local calendar days -> start-inclusive/end-exclusive UTC range in the tenant reporting timezone.</summary>
    public static AnalyticsRange ResolveRange(AnalyticsFilter f, DateTime nowUtc, TimeZoneInfo tz)
    {
        var to = f.To ?? ReportingCalendar.LocalDate(nowUtc, tz);
        var from = f.From ?? to.AddDays(-29);
        if (from > to)
        {
            throw AppException.Validation("From", "The start date must not be after the end date.");
        }

        var days = to.DayNumber - from.DayNumber + 1;
        if (days > AnalyticsRange.MaxDays)
        {
            throw AppException.Validation("To", $"The range cannot exceed {AnalyticsRange.MaxDays} days.");
        }

        var (fromUtc, toUtc) = ReportingCalendar.ToUtcRange(from, to, tz);
        return new AnalyticsRange(fromUtc, toUtc, days > AnalyticsRange.MonthlyAfterDays);
    }

    // ---------------------------------------------------------------- dashboard

    public async Task<DashboardDto> GetDashboardAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        var now = Now;
        var tz = await zones.GetAsync(ct);
        var range = ResolveRange(filter, now, tz);
        var scopeFilter = filter with { AgentMembershipId = null, CustomerId = null, Status = null };
        var scope = Scope(access.Visible(actor), scopeFilter);
        var open = scope.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        var today = ReportingCalendar.LocalDate(now, tz);
        var todayStart = ReportingCalendar.LocalDayStartUtc(today, tz);
        var todayEnd = ReportingCalendar.LocalDayStartUtc(today.AddDays(1), tz);
        var cycles = CyclesOf(scope, IsUnrestricted(actor, scopeFilter));

        var resolvedToday = await cycles.Where(c => c.ResolvedAtUtc >= todayStart && c.ResolvedAtUtc < todayEnd).Select(c => c.TicketId).Distinct().CountAsync(ct);
        var openCount = await open.CountAsync(ct);

        int unassigned = 0, overdue = 0, firstSample = 0;
        SlaComplianceDto? sla = null;
        double? avgFirst = null;
        IReadOnlyList<WorkloadDto>? workload = null;
        if (!actor.IsCustomer)
        {
            var effective = access.EffectiveAssignments();
            unassigned = await open.CountAsync(t => !effective.Any(a => a.TicketId == t.Id), ct);
            var overdueIds = OverdueTicketIds(now);
            overdue = await open.CountAsync(t => overdueIds.Contains(t.Id), ct);
            sla = await SlaAsync(cycles, range, ct);
            var first = await StatsAsync(cycles.Where(c => c.StartedAtUtc >= range.FromUtc && c.StartedAtUtc < range.ToUtc && c.FirstResponseAtUtc != null), FirstResponseMinutes, false, ct);
            (firstSample, avgFirst) = (first.Count, first.Average);
            workload = actor.IsManager ? await WorkloadAsync(ct) : null;
        }

        var created = await scope.CountAsync(t => t.CreatedAtUtc >= range.FromUtc && t.CreatedAtUtc < range.ToUtc, ct);
        var trend = await TrendAsync(scope, cycles, range, tz, ct);
        var byStatus = (await open.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).OrderBy(x => x.Key).Select(x => new CountByDto(x.Key.ToString(), x.N)).ToList();
        var byPriority = (await scope.Where(t => t.CreatedAtUtc >= range.FromUtc && t.CreatedAtUtc < range.ToUtc).GroupBy(t => t.Priority).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .OrderBy(x => x.Key).Select(x => new CountByDto(x.Key.ToString(), x.N)).ToList();

        return new DashboardDto(now, tz.Id, range.FromUtc, range.ToUtc, range.Monthly ? "month" : "day", new CurrentStateDto(openCount, unassigned, overdue, resolvedToday),
            created, trend.Sum(p => p.Resolved), avgFirst, firstSample, sla, trend, byStatus, byPriority, workload);
    }

    private static async Task<SlaComplianceDto> SlaAsync(IQueryable<TicketSlaCycle> cycles, AnalyticsRange range, CancellationToken ct)
    {
        var done = cycles.Where(c => c.ResolvedAtUtc >= range.FromUtc && c.ResolvedAtUtc < range.ToUtc);
        var counts = await done.GroupBy(_ => 1).Select(g => new { Total = g.Count(), Met = g.Count(c => c.ResolvedAtUtc <= c.ResolutionDueAtUtc) }).SingleOrDefaultAsync(ct);
        var (total, met) = (counts?.Total ?? 0, counts?.Met ?? 0);
        return new SlaComplianceDto(total, met, total - met, total == 0 ? null : Math.Round(100.0 * met / total, 1));
    }

    private async Task<List<WorkloadDto>> WorkloadAsync(CancellationToken ct)
    {
        var agents = await (from m in db.Memberships.AsNoTracking()
                            join u in db.Users on m.UserId equals u.Id
                            where m.Role == TenantRole.Agent && m.Status == MembershipStatus.Active && u.Status == UserStatus.Active
                            select new { m.Id, u.DisplayName }).ToListAsync(ct);
        // Eligibility (active Agent + active user) is decided by the `agents` list above; this join only counts open assignments of OPEN tickets.
        var counts = (await (from a in db.TicketAssignments
                             where a.UnassignedAtUtc == null
                             join t in db.Tickets on a.TicketId equals t.Id
                             where t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed
                             group a by a.MembershipId into g
                             select new { Id = g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N);
        return agents.Select(a => new WorkloadDto(a.Id, a.DisplayName, counts.GetValueOrDefault(a.Id))).OrderByDescending(w => w.ActiveTickets).ThenBy(w => w.DisplayName).ToList();
    }

    // ---------------------------------------------------------------- reports (Owner/Manager only)

    public async Task<ReportDto> GetReportAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var actor = await RequireManagerAsync(ct);
        var now = Now;
        var tz = await zones.GetAsync(ct);
        var range = ResolveRange(filter, now, tz);
        var scope = Scope(access.Visible(actor), filter);
        var inRange = scope.Where(t => t.CreatedAtUtc >= range.FromUtc && t.CreatedAtUtc < range.ToUtc);
        var unrestricted = IsUnrestricted(actor, filter);
        var cycles = CyclesOf(scope, unrestricted);
        var completed = cycles.Where(c => c.ResolvedAtUtc >= range.FromUtc && c.ResolvedAtUtc < range.ToUtc);

        var stats = await StatsAsync(completed, ResolutionMinutes, true, ct);
        var overdueIds = OverdueTicketIds(now);
        var openOverdue = await scope.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && overdueIds.Contains(t.Id), ct);

        // Reopen cohort: completed cycles in range followed by a later cycle (a reopen) that started before the range end.
        var reopened = await completed.CountAsync(c => db.TicketSlaCycles.Any(n => n.TicketId == c.TicketId && n.CycleNumber == c.CycleNumber + 1 && n.StartedAtUtc < range.ToUtc), ct);
        var reopenEvents = await cycles.CountAsync(c => c.Reason == SlaCycleReason.Reopened && c.StartedAtUtc >= range.FromUtc && c.StartedAtUtc < range.ToUtc, ct);

        var total = await inRange.CountAsync(ct);
        var categories = await (from t in inRange
                                join c in db.TicketCategories on t.CategoryId equals c.Id
                                group t by new { c.Id, c.Name } into g
                                select new { g.Key.Id, g.Key.Name, N = g.Count() }).ToListAsync(ct);

        // Attribution = the actor of the persisted "Resolved" timeline entry (who actually resolved it), never the current assignees.
        var resolvers = (await db.TicketActivities.AsNoTracking()
            .Where(a => a.Type == TicketActivityType.Resolved && a.OccurredAtUtc >= range.FromUtc && a.OccurredAtUtc < range.ToUtc && a.ActorUserId != null)
            .Where(a => unrestricted || scope.Any(t => t.Id == a.TicketId))
            .GroupBy(a => a.ActorUserId).Select(g => new { UserId = g.Key!.Value, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.UserId, x => x.N);
        var workload = await WorkloadAsync(ct);
        var agentUsers = (await db.Memberships.AsNoTracking().Where(m => m.Role == TenantRole.Agent).Select(m => new { m.Id, m.UserId }).ToListAsync(ct)).ToDictionary(m => m.Id, m => m.UserId);
        var agents = workload
            .Select(w => new AgentPerformanceDto(w.MembershipId, w.DisplayName, w.ActiveTickets, agentUsers.TryGetValue(w.MembershipId, out var uid) ? resolvers.GetValueOrDefault(uid) : 0))
            .Where(a => filter.AgentMembershipId is null || a.MembershipId == filter.AgentMembershipId).ToList();

        var topCustomers = await (from t in inRange
                                  join c in db.Customers on t.CustomerId equals c.Id
                                  group t by new { c.Id, c.Name } into g
                                  orderby g.Count() descending, g.Key.Name
                                  select new
                                  {
                                      g.Key.Id,
                                      g.Key.Name,
                                      Tickets = g.Count(),
                                      Open = g.Count(x => x.Status != TicketStatus.Resolved && x.Status != TicketStatus.Closed),
                                      Resolved = g.Count(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed),
                                  }).Take(50).ToListAsync(ct);
        // Overdue per customer in ONE grouped query for the (at most 50) shown customers, instead of a correlated subquery per ticket.
        var customerIds = topCustomers.Select(c => (Guid?)c.Id).ToList();
        var overdueByCustomer = (await inRange
            .Where(t => customerIds.Contains(t.CustomerId) && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && overdueIds.Contains(t.Id))
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N);
        var customers = topCustomers.Select(c => new CustomerAnalysisDto(c.Id, c.Name, c.Tickets, c.Open, c.Resolved, overdueByCustomer.GetValueOrDefault(c.Id))).ToList();

        return new ReportDto(now, tz.Id, range.FromUtc, range.ToUtc, range.Monthly ? "month" : "day", total, await TrendAsync(scope, cycles, range, tz, ct),
            (await inRange.GroupBy(t => t.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).OrderBy(x => x.Key).Select(x => new CountByDto(x.Key.ToString(), x.N)).ToList(),
            (await inRange.GroupBy(t => t.Priority).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).OrderBy(x => x.Key).Select(x => new CountByDto(x.Key.ToString(), x.N)).ToList(),
            new ResolutionReportDto(stats.Count, stats.Average, stats.Median), await SlaAsync(cycles, range, ct), openOverdue,
            new ReopenReportDto(stats.Count, reopened, stats.Count == 0 ? null : Math.Round(100.0 * reopened / stats.Count, 1), reopenEvents),
            agents, categories.OrderByDescending(c => c.N).ThenBy(c => c.Name).Select(c => new CategoryShareDto(c.Id, c.Name, c.N, total == 0 ? 0 : Math.Round(100.0 * c.N / total, 1))).ToList(), customers);
    }

    // ---------------------------------------------------------------- CSV export (streamed)

    public async Task<CsvExport> ExportAsync(ReportExportKind kind, AnalyticsFilter filter, CancellationToken ct)
    {
        var actor = await RequireManagerAsync(ct);
        var tz = await zones.GetAsync(ct);
        var range = ResolveRange(filter, Now, tz);
        var stamp = $"{ReportingCalendar.LocalDate(range.FromUtc, tz):yyyyMMdd}-{ReportingCalendar.LocalDate(range.ToUtc.AddTicks(-1), tz):yyyyMMdd}";
        if (kind != ReportExportKind.Tickets)
        {
            var report = await GetReportAsync(filter, ct);
            var (name, headers, rows) = kind switch
            {
                ReportExportKind.Agents => ($"agents-{stamp}.csv", (IReadOnlyList<string>)["agent", "current_workload", "resolved_in_period"],
                    report.Agents.Select(a => (IReadOnlyList<object?>)[a.DisplayName, a.CurrentWorkload, a.ResolvedInPeriod])),
                ReportExportKind.Categories => ($"categories-{stamp}.csv", (IReadOnlyList<string>)["category", "tickets", "share_percent"],
                    report.Categories.Select(c => (IReadOnlyList<object?>)[c.Name, c.Count, c.SharePercent])),
                _ => ($"customers-{stamp}.csv", (IReadOnlyList<string>)["customer", "tickets", "open", "resolved", "open_overdue"],
                    report.Customers.Select(c => (IReadOnlyList<object?>)[c.Name, c.Tickets, c.Open, c.Resolved, c.OverdueOpen])),
            };
            var content = CsvWriter.Build(headers, rows);
            return new CsvExport(name, false, (stream, token) => CsvWriter.WriteUtf8Async(stream, content, token));
        }

        var scope = Scope(access.Visible(actor), filter).Where(t => t.CreatedAtUtc >= range.FromUtc && t.CreatedAtUtc < range.ToUtc);
        var truncated = await scope.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Number).Select(t => t.Id).Skip(MaxExportRows).Take(1).AnyAsync(ct);
        var prefix = await db.Settings.AsNoTracking().Select(s => s.TicketNumberPrefix).SingleOrDefaultAsync(ct) ?? TenantSettings.DefaultTicketPrefix;
        var query = (from t in scope
                     orderby t.CreatedAtUtc, t.Number
                     select new
                     {
                         t.Number, t.Title, t.Status, t.Priority, t.CreatedAtUtc, t.ResolvedAtUtc,
                         Category = db.TicketCategories.Where(c => c.Id == t.CategoryId).Select(c => c.Name).FirstOrDefault(),
                         Customer = db.Customers.Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefault(),
                     }).Take(MaxExportRows).AsNoTracking();

        return new CsvExport($"tickets-{stamp}.csv", truncated, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true), 16 * 1024, leaveOpen: true);
            await writer.WriteAsync(CsvWriter.Build(["ticket", "title", "status", "priority", "category", "customer", "created_at_utc", "resolved_at_utc"], []).AsMemory(), token);
            await foreach (var r in query.AsAsyncEnumerable().WithCancellation(token))
            {
                await writer.WriteAsync(CsvWriter.Row([TenantSettings.FormatNumber(prefix, r.Number), r.Title, r.Status, r.Priority, r.Category, r.Customer, r.CreatedAtUtc, r.ResolvedAtUtc]).AsMemory(), token);
            }
        });
    }

    // ---------------------------------------------------------------- shared

    private async Task<TicketActor> RequireManagerAsync(CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        return actor.IsManager ? actor : throw AppException.Forbidden("Reports are available to Owners and Managers only.");
    }

    /// <summary>Owner/Manager without any ticket-level filter see every ticket of the (query-filtered) tenant, so cycle/activity queries need no semi-join back to Tickets.</summary>
    private static bool IsUnrestricted(TicketActor actor, AnalyticsFilter f) =>
        actor.IsManager && f is { Priority: null, CategoryId: null, Status: null, CustomerId: null, AgentMembershipId: null };

    private IQueryable<TicketSlaCycle> CyclesOf(IQueryable<Ticket> scope, bool unrestricted) =>
        unrestricted ? db.TicketSlaCycles.AsNoTracking() : db.TicketSlaCycles.AsNoTracking().Where(c => scope.Any(t => t.Id == c.TicketId));

    /// <summary>Tickets whose ACTIVE cycle is past its resolution deadline; used as IN (subquery) so PostgreSQL can hash-semi-join it.</summary>
    private IQueryable<Guid> OverdueTicketIds(DateTime now) =>
        db.TicketSlaCycles.Where(c => c.ResolvedAtUtc == null && c.ResolutionDueAtUtc < now).Select(c => c.TicketId);

    private IQueryable<Ticket> Scope(IQueryable<Ticket> visible, AnalyticsFilter f)
    {
        var q = visible;
        if (f.Priority is { } p)
        {
            q = q.Where(t => t.Priority == p);
        }

        if (f.CategoryId is { } c)
        {
            q = q.Where(t => t.CategoryId == c);
        }

        if (f.Status is { } s)
        {
            q = q.Where(t => t.Status == s);
        }

        if (f.CustomerId is { } cu)
        {
            q = q.Where(t => t.CustomerId == cu);
        }

        if (f.AgentMembershipId is { } a)
        {
            q = q.Where(t => db.TicketAssignments.Any(x => x.TicketId == t.Id && x.MembershipId == a && x.UnassignedAtUtc == null));
        }

        return q;
    }

    private sealed record DurationStats(int Count, double? Average, double? Median);

    /// <summary>Count + average (+ exact median) of a duration, computed by PostgreSQL: no per-row transfer, no sampling cap.</summary>
    private async Task<DurationStats> StatsAsync(IQueryable<TicketSlaCycle> cycles, Expression<Func<TicketSlaCycle, double>> minutes, bool withMedian, CancellationToken ct)
    {
        if (!db.IsPostgreSql)
        {
            // SQLite is the test provider only: same expression, evaluated over the FULL population in memory.
            var all = (await cycles.ToListAsync(ct)).Select(minutes.Compile()).Order().ToList();
            return new DurationStats(all.Count, all.Count == 0 ? null : all.Average(), withMedian ? Median(all) : null);
        }

        var count = await cycles.CountAsync(ct);
        if (count == 0)
        {
            return new DurationStats(0, null, null);
        }

        var durations = cycles.Select(minutes);
        var average = await durations.Select(m => (double?)m).AverageAsync(ct);
        double? median = null;
        if (withMedian)
        {
            // middle row(s) by ORDER BY ... OFFSET/LIMIT: one or two values travel, never the sample.
            var middle = await durations.OrderBy(m => m).Skip((count - 1) / 2).Take(count % 2 == 1 ? 1 : 2).ToListAsync(ct);
            median = middle.Average();
        }

        return new DurationStats(count, average, median);
    }

    /// <summary>
    /// Created/resolved/avg-resolution per tenant-local day (or month) bucket. The range is split at UTC-offset changes (DST); inside a
    /// segment the offset is constant, so GROUP BY (utc + offset) is exact. Zero-count buckets are filled in so the series has no gaps.
    /// </summary>
    private async Task<List<TrendPointDto>> TrendAsync(IQueryable<Ticket> scope, IQueryable<TicketSlaCycle> cycles, AnalyticsRange range, TimeZoneInfo tz, CancellationToken ct)
    {
        var monthly = range.Monthly;
        var created = new Dictionary<DateOnly, int>();
        var resolved = new Dictionary<DateOnly, (int Count, double Sum)>();
        foreach (var segment in ReportingCalendar.Segments(range.FromUtc, range.ToUtc, tz))
        {
            var (start, end, off) = (segment.StartUtc, segment.EndUtc, segment.Offset.TotalMinutes);
            var createdRows = await scope.Where(t => t.CreatedAtUtc >= start && t.CreatedAtUtc < end)
                .GroupBy(t => new { Y = t.CreatedAtUtc.AddMinutes(off).Year, M = t.CreatedAtUtc.AddMinutes(off).Month, D = monthly ? 1 : t.CreatedAtUtc.AddMinutes(off).Day })
                .Select(g => new { g.Key.Y, g.Key.M, g.Key.D, N = g.Count() }).ToListAsync(ct);
            foreach (var r in createdRows)
            {
                var key = new DateOnly(r.Y, r.M, r.D);
                created[key] = created.GetValueOrDefault(key) + r.N;
            }

            var done = cycles.Where(c => c.ResolvedAtUtc >= start && c.ResolvedAtUtc < end);
            if (db.IsPostgreSql)
            {
                var rows = await done
                    .GroupBy(c => new { Y = c.ResolvedAtUtc!.Value.AddMinutes(off).Year, M = c.ResolvedAtUtc!.Value.AddMinutes(off).Month, D = monthly ? 1 : c.ResolvedAtUtc!.Value.AddMinutes(off).Day })
                    .Select(g => new { g.Key.Y, g.Key.M, g.Key.D, N = g.Count(), Sum = g.Sum(c => (c.ResolvedAtUtc!.Value - c.StartedAtUtc).TotalMinutes) }).ToListAsync(ct);
                foreach (var r in rows)
                {
                    Add(resolved, new DateOnly(r.Y, r.M, r.D), r.N, r.Sum);
                }
            }
            else
            {
                var minutes = ResolutionMinutes.Compile();
                foreach (var c in await done.ToListAsync(ct)) // SQLite (tests) only
                {
                    var local = c.ResolvedAtUtc!.Value.AddMinutes(off);
                    Add(resolved, new DateOnly(local.Year, local.Month, monthly ? 1 : local.Day), 1, minutes(c));
                }
            }
        }

        var points = new List<TrendPointDto>();
        var cursor = ReportingCalendar.LocalDate(range.FromUtc, tz);
        var last = ReportingCalendar.LocalDate(range.ToUtc.AddTicks(-1), tz);
        if (monthly)
        {
            cursor = new DateOnly(cursor.Year, cursor.Month, 1);
        }

        for (; cursor <= last; cursor = monthly ? cursor.AddMonths(1) : cursor.AddDays(1))
        {
            var res = resolved.GetValueOrDefault(cursor);
            points.Add(new TrendPointDto(ReportingCalendar.LocalDayStartUtc(cursor, tz), created.GetValueOrDefault(cursor), res.Count, res.Count == 0 ? null : res.Sum / res.Count));
        }

        return points;
    }

    private static void Add(Dictionary<DateOnly, (int Count, double Sum)> map, DateOnly key, int count, double sum)
    {
        var (c, s) = map.GetValueOrDefault(key);
        map[key] = (c + count, s + sum);
    }

    private static double? Median(List<double> sorted) =>
        sorted.Count == 0 ? null : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
