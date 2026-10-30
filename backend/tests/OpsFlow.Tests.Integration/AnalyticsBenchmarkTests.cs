#pragma warning disable CA1305 // report text only (benchmark output), culture is irrelevant here
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// Repeatable PostgreSQL measurements on an isolated Testcontainers database (never a developer database).
/// OPSFLOW_TEST_DB=postgres runs it; OPSFLOW_BENCH_100K=1 adds the 100,000-ticket step; the report is written to
/// OPSFLOW_BENCH_OUT (default: %TEMP%/opsflow-analytics-bench.md). Latency is recorded, not asserted (CI noise); query counts and totals ARE asserted.
/// </summary>
public class AnalyticsBenchmarkTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const int Warmups = 2;
    private const int Runs = 15;

    // Candidate (NOT in the model/migrations): evaluated here only to decide whether a migration is worth proposing.
    private static readonly string[] CandidateIndexes =
    [
        "CREATE INDEX \"IX_Tickets_TenantId_UpdatedAtUtc_Number\" ON \"Tickets\" (\"TenantId\", \"UpdatedAtUtc\" DESC, \"Number\")",
        "CREATE INDEX \"IX_TicketSlaCycles_TenantId_ResolvedAtUtc\" ON \"TicketSlaCycles\" (\"TenantId\", \"ResolvedAtUtc\") WHERE \"ResolvedAtUtc\" IS NOT NULL",
        "CREATE INDEX \"IX_TicketActivities_TenantId_Type_OccurredAtUtc\" ON \"TicketActivities\" (\"TenantId\", \"Type\", \"OccurredAtUtc\")",
    ];

    private sealed record Op(string Name, Func<TicketWorld, Task<int>> Run);

    private static string Gql(Guid tenant) => $"/api/v1/tenants/{tenant}/graphql";

    private static Func<TicketWorld, Task<int>> Graph(string query) => async w =>
    {
        var response = await w.Manager.PostAsync(Gql(w.Org.Id), new { query });
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.False(JsonDocument.Parse(bytes).RootElement.TryGetProperty("errors", out var errors), errors.ToString());
        return bytes.Length;
    };

    private static readonly Op[] Ops =
    [
        new("dashboard (all KPIs, trend, workload)", Graph("{ dashboard { current { openTickets unassignedTickets overdueTickets resolvedToday } createdInPeriod resolvedInPeriod avgFirstResponseMinutes firstResponseSample sla { eligibleCycles met breached compliancePercent } trend { periodStartUtc created resolved avgResolutionMinutes } openByStatus { key count } byPriority { key count } workload { displayName activeTickets } } }")),
        new("report (all sections)", Graph("{ report { ticketsCreated trend { created resolved avgResolutionMinutes } byStatus { key count } byPriority { key count } resolution { completedCycles avgMinutes medianMinutes } sla { eligibleCycles met } openOverdueTickets reopen { resolvedCycles reopenedCycles reopenRatePercent reopenEventsInPeriod } agents { displayName currentWorkload resolvedInPeriod } categories { name count sharePercent } customers { name tickets open resolved overdueOpen } } }")),
        new("tickets page (20, updatedAt desc)", Graph("{ tickets(filter: { pageSize: 20, sortBy: \"updatedAt\" }) { totalCount items { id displayNumber title status priority categoryName customerName assigneeCount resolutionDueAtUtc isOverdue } } }")),
        new("kanban (5 columns x 20)", Graph("{ kanbanBoard(perColumn: 20) { status total tickets { id title assigneeCount isOverdue } } }")),
        new("ticket + SLA history", async w =>
        {
            var first = JsonDocument.Parse(await (await w.Manager.PostAsync(Gql(w.Org.Id), new { query = "{ tickets(filter: { pageSize: 1, statuses: [ASSIGNED] }) { items { id } } }" })).Content.ReadAsStringAsync());
            var id = first.RootElement.GetProperty("data").GetProperty("tickets").GetProperty("items")[0].GetProperty("id").GetString();
            return await Graph($"{{ ticket(id: \"{id}\") {{ assignees {{ displayName }} currentSla {{ cycleNumber }} }} ticketSla(ticketId: \"{id}\") {{ cycles {{ id cycleNumber }} }} }}")(w);
        }),
        new("csv export tickets (90 days, streamed)", async w =>
        {
            var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-89);
            var response = await w.Manager.GetAsync(w.Url($"reports/export?kind=Tickets&from={from:yyyy-MM-dd}"));
            return (await response.Content.ReadAsByteArrayAsync()).Length;
        }),
    ];

    [PostgresOnlyFact]
    public async Task Measure_analytics_at_increasing_tenant_sizes()
    {
        var sizes = Environment.GetEnvironmentVariable("OPSFLOW_BENCH_100K") == "1" ? new[] { 1_000, 10_000, 100_000 } : [1_000, 10_000];
        var w = await TicketWorld.CreateAsync(factory);
        var report = new StringBuilder("# Analytics benchmark (PostgreSQL 17 Testcontainer, warm, sequential requests)\n\n");
        var queryCounts = new Dictionary<string, List<int>>();
        var seeded = 0;
        SeedTotals? totals = null;

        foreach (var size in sizes)
        {
            var seedWatch = Stopwatch.StartNew();
            var step = await BulkSeed.AddTicketsAsync(w, size - seeded, seeded + 1);
            totals = Merge(totals, step);
            seeded = size;
            report.Append($"## {size:N0} tickets ({totals.CompletedCycles:N0} completed cycles, {totals.Assignments:N0} assignments; seeded in {seedWatch.Elapsed.TotalSeconds:F1}s)\n\n| operation | queries | p50 ms | p95 ms | bytes |\n|---|---|---|---|---|\n");

            await MeasureTableAsync(w, report, queryCounts);

            report.Append('\n');
            await AssertTotalsAsync(w, totals);
        }

        await AppendEmulationAndPlansAsync(w, report, totals!);
        if (Environment.GetEnvironmentVariable("OPSFLOW_BENCH_INDEXES") == "1")
        {
            await using (var connection = new NpgsqlConnection(factory.PostgresConnectionString))
            {
                await connection.OpenAsync();
                foreach (var sql in CandidateIndexes)
                {
                    await using var create = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
                    await create.ExecuteNonQueryAsync();
                }

                await using var analyze = new NpgsqlCommand("ANALYZE", connection) { CommandTimeout = 300 };
                await analyze.ExecuteNonQueryAsync();
            }

            report.Append("## WHAT-IF: with candidate indexes (created only in this isolated test database)\n\n| operation | queries | p50 ms | p95 ms | bytes |\n|---|---|---|---|---|\n");
            await MeasureTableAsync(w, report, new Dictionary<string, List<int>>());
            await AppendEmulationAndPlansAsync(w, report, totals!);
        }

        var path = Environment.GetEnvironmentVariable("OPSFLOW_BENCH_OUT") ?? Path.Combine(Path.GetTempPath(), "opsflow-analytics-bench.md");
        await File.WriteAllTextAsync(path, report.ToString());

        foreach (var (name, counts) in queryCounts)
        {
            Assert.True(counts.Distinct().Count() == 1, $"{name}: query count grew with data size: {string.Join(',', counts)}");
        }
    }


    private async Task MeasureTableAsync(TicketWorld w, StringBuilder report, Dictionary<string, List<int>> queryCounts)
    {
        foreach (var op in Ops)
        {
            for (var i = 0; i < Warmups; i++)
            {
                await op.Run(w);
            }

            var times = new List<double>();
            var queries = 0;
            var bytes = 0;
            for (var i = 0; i < Runs; i++)
            {
                var watch = Stopwatch.StartNew();
                queries = await factory.Queries.MeasureAsync(async () => bytes = await op.Run(w));
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            if (!queryCounts.TryGetValue(op.Name, out var list))
            {
                queryCounts[op.Name] = list = [];
            }

            list.Add(queries);
            report.Append($"| {op.Name} | {queries} | {times[Runs / 2]:F0} | {times[(int)Math.Ceiling(Runs * 0.95) - 1]:F0} | {bytes:N0} |\n");
        }
    }

    private static SeedTotals Merge(SeedTotals? a, SeedTotals b)
    {
        if (a is null)
        {
            return b;
        }

        a.Tickets += b.Tickets;
        a.Open += b.Open;
        a.OpenUnassigned += b.OpenUnassigned;
        a.OpenOverdue += b.OpenOverdue;
        a.CompletedCycles += b.CompletedCycles;
        a.CompletedCyclesMet += b.CompletedCyclesMet;
        a.ReopenedCycles += b.ReopenedCycles;
        a.Assignments += b.Assignments;
        a.OpenAssignments += b.OpenAssignments;
        a.CompletedMinutesSum += b.CompletedMinutesSum;
        a.CompletedMinutes.AddRange(b.CompletedMinutes);
        return a;
    }

    private static async Task AssertTotalsAsync(TicketWorld w, SeedTotals t)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-89);
        var body = JsonDocument.Parse(await (await w.Manager.PostAsync(Gql(w.Org.Id), new { query = $"{{ dashboard(filter: {{ from: \"{from:yyyy-MM-dd}\" }}) {{ current {{ openTickets unassignedTickets overdueTickets }} createdInPeriod resolvedInPeriod trend {{ created resolved }} }} }}" })).Content.ReadAsStringAsync());
        var d = body.RootElement.GetProperty("data").GetProperty("dashboard");
        Assert.Equal(t.Open, d.GetProperty("current").GetProperty("openTickets").GetInt32());
        Assert.Equal(t.OpenUnassigned, d.GetProperty("current").GetProperty("unassignedTickets").GetInt32());
        Assert.Equal(t.OpenOverdue, d.GetProperty("current").GetProperty("overdueTickets").GetInt32());
        Assert.Equal(t.Tickets, d.GetProperty("createdInPeriod").GetInt32());
        Assert.Equal(t.CompletedCycles, d.GetProperty("resolvedInPeriod").GetInt32());
        Assert.Equal(t.Tickets, d.GetProperty("trend").EnumerateArray().Sum(p => p.GetProperty("created").GetInt32())); // nothing truncated at any size
    }

    /// <summary>Times the REMOVED approach (materialize every resolved cycle's two timestamps) for comparison, then EXPLAINs what the new code actually sends.</summary>
    private async Task AppendEmulationAndPlansAsync(TicketWorld w, StringBuilder report, SeedTotals totals)
    {
        report.Append("## Removed approach, emulated (client-side durations)\n\n");
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OpsFlow.Application.Abstractions.TenantContext>().Set(w.Org.Id, w.ManagerMembership, OpsFlow.Domain.Tenants.TenantRole.Manager);
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var watch = Stopwatch.StartNew();
            var rows = await db.TicketSlaCycles.AsNoTracking().Where(c => c.ResolvedAtUtc != null).Select(c => new { c.StartedAtUtc, c.ResolvedAtUtc }).ToListAsync();
            var avg = rows.Average(r => (r.ResolvedAtUtc!.Value - r.StartedAtUtc).TotalMinutes);
            report.Append($"materializing {rows.Count:N0} resolved cycles to average in C#: {watch.Elapsed.TotalMilliseconds:F0} ms (avg {avg:F1} min); the previous code additionally capped this at 50,000 rows silently.\n\n");
        }

        report.Append("## EXPLAIN (ANALYZE, BUFFERS) of the statements the new code sends\n\n");
        factory.Queries.Capture = true;
        foreach (var op in Ops.Take(4))
        {
            factory.Queries.Reset();
            await op.Run(w);
            var plans = new List<(double Ms, string Summary)>();
            await using var connection = new NpgsqlConnection(factory.PostgresConnectionString);
            await connection.OpenAsync();
            foreach (var cmd in factory.Queries.Captured.Where(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)))
            {
                await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + cmd.Sql, connection);
                foreach (var p in cmd.Parameters.OfType<NpgsqlParameter>())
                {
                    explain.Parameters.Add(p.Clone());
                }

                var lines = new List<string>();
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }

                var ms = double.Parse(lines.Last(l => l.StartsWith("Execution Time", StringComparison.Ordinal)).Split(':')[1].Replace("ms", "", StringComparison.Ordinal).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var scans = lines.Where(l => l.Contains("Seq Scan", StringComparison.Ordinal) || l.Contains("Index", StringComparison.Ordinal) || l.Contains("Bitmap", StringComparison.Ordinal))
                    .Select(l => l.Trim().Split("  (")[0].TrimStart('-', '>', ' ')).Distinct().Take(6);
                plans.Add((ms, $"{string.Join("; ", scans)} :: {cmd.Sql.Split('\n')[0][..Math.Min(70, cmd.Sql.Split('\n')[0].Length)]}"));
            }

            report.Append($"### {op.Name}: {plans.Count} statements, total execution {plans.Sum(p => p.Ms):F1} ms\n");
            foreach (var (ms, summary) in plans.OrderByDescending(p => p.Ms).Take(4))
            {
                report.Append($"- {ms:F1} ms — {summary}\n");
            }

            report.Append('\n');
        }

        factory.Queries.Capture = false;
        Assert.True(totals.Tickets > 0);
    }
}
