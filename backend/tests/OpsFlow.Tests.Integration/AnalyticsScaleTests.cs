using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Application.Analytics;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Tenant timezone used by the DST/boundary tests (the production provider answers UTC until a tenant setting exists).</summary>
public sealed class RomeApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<IReportingTimeZoneProvider, RomeProvider>()));
    }

    private sealed class RomeProvider : IReportingTimeZoneProvider
    {
        public Task<TimeZoneInfo> GetAsync(CancellationToken ct) => Task.FromResult(ReportingCalendar.Resolve("Europe/Rome"));
    }
}

/// <summary>GraphQL cost/paging limits, N+1 query-count stability, exact aggregates at scale, median and timezone buckets (runs on SQLite and PostgreSQL).</summary>
public class AnalyticsScaleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> GqlAsync(ApiClient who, Guid tenantId, string query)
    {
        var response = await who.PostAsync($"/api/v1/tenants/{tenantId}/graphql", new { query });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("errors", out var e) && e.GetArrayLength() > 0 && e[0].TryGetProperty("extensions", out var x) && x.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static string Aliased(string field, int n) => "{ " + string.Join(' ', Enumerable.Range(0, n).Select(i => $"a{i}: {field}")) + " }";

    // ---------------------------------------------------------------- limits

    [Fact]
    public async Task Approved_combined_operation_fits_inside_the_cost_budget()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var body = await GqlAsync(w.Manager, w.Org.Id,
            "{ dashboard { timeZone } report { ticketsCreated } kanbanBoard { status total } tickets { totalCount } customers { totalCount } teamMembers { totalCount } }");
        Assert.Null(Code(body));
        Assert.True(body.GetProperty("data").GetProperty("dashboard").GetProperty("timeZone").GetString() is "UTC");
    }

    [Fact]
    public async Task Repeated_aliases_of_expensive_fields_are_costed_and_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var counted = await factory.Queries.MeasureAsync(async () =>
        {
            var heavy = await GqlAsync(w.Manager, w.Org.Id, Aliased("report { ticketsCreated }", 20)); // 20 x weight 60 = 1200 > 250
            Assert.True(heavy.TryGetProperty("errors", out _));
            Assert.True(!heavy.TryGetProperty("data", out var d) || d.ValueKind == JsonValueKind.Null);
        });
        Assert.True(counted <= 3, $"a rejected operation must not execute resolvers (auth only), but ran {counted} commands");

        Assert.True((await GqlAsync(w.Manager, w.Org.Id, Aliased("dashboard { timeZone }", 10))).TryGetProperty("errors", out _)); // 300 > 250
        Assert.Null(Code(await GqlAsync(w.Manager, w.Org.Id, Aliased("dashboard { timeZone }", 5)))); // 150 fits
    }

    [Theory]
    [InlineData("{ tickets(filter: { pageSize: 1000 }) { totalCount } }")]
    [InlineData("{ tickets(filter: { pageSize: 0 }) { totalCount } }")]
    [InlineData("{ tickets(filter: { page: 0 }) { totalCount } }")]
    [InlineData("{ customers(pageSize: 101) { totalCount } }")]
    [InlineData("{ teamMembers(pageSize: -1) { totalCount } }")]
    [InlineData("{ kanbanBoard(perColumn: 51) { total } }")]
    public async Task Out_of_range_paging_is_rejected_not_clamped(string query)
    {
        var w = await TicketWorld.CreateAsync(factory);
        Assert.Equal("VALIDATION_ERROR", Code(await GqlAsync(w.Manager, w.Org.Id, query)));
    }

    [Fact]
    public async Task Maximum_allowed_page_is_accepted_and_deterministic()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddTicketsAsync(w, 130, 1);
        var first = await GqlAsync(w.Manager, w.Org.Id, "{ tickets(filter: { pageSize: 100, sortBy: \"number\", descending: false }) { totalCount items { number } } }");
        var again = await GqlAsync(w.Manager, w.Org.Id, "{ tickets(filter: { pageSize: 100, sortBy: \"number\", descending: false }) { totalCount items { number } } }");
        var page = first.GetProperty("data").GetProperty("tickets");
        Assert.Equal(130, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.Equal(first.ToString(), again.ToString());
    }

    // ---------------------------------------------------------------- N+1

    [Fact]
    public async Task Query_count_does_not_grow_with_the_number_of_returned_records()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddTicketsAsync(w, 1000, 1);
        var counts = new Dictionary<string, List<int>>();
        foreach (var size in new[] { 1, 10, 100 })
        {
            Record(counts, "tickets", await factory.Queries.MeasureAsync(() => GqlAsync(w.Manager, w.Org.Id, $"{{ tickets(filter: {{ pageSize: {size} }}) {{ totalCount items {{ id assigneeCount categoryName customerName resolutionDueAtUtc isOverdue }} }} }}")));
            Record(counts, "customers", await factory.Queries.MeasureAsync(() => GqlAsync(w.Manager, w.Org.Id, $"{{ customers(pageSize: {size}) {{ items {{ id name }} }} }}")));
        }

        foreach (var perColumn in new[] { 1, 10, 50 })
        {
            Record(counts, "kanban", await factory.Queries.MeasureAsync(() => GqlAsync(w.Manager, w.Org.Id, $"{{ kanbanBoard(perColumn: {perColumn}) {{ status total tickets {{ id assigneeCount }} }} }}")));
        }

        var ticketId = Guid.Parse((await GqlAsync(w.Manager, w.Org.Id, "{ tickets(filter: { pageSize: 1, statuses: [IN_PROGRESS] }) { items { id } } }")).GetProperty("data").GetProperty("tickets").GetProperty("items")[0].GetProperty("id").GetString()!);
        Record(counts, "ticket", await factory.Queries.MeasureAsync(() => GqlAsync(w.Manager, w.Org.Id, $"{{ ticket(id: \"{ticketId}\") {{ assignees {{ displayName }} currentSla {{ cycleNumber }} }} ticketSla(ticketId: \"{ticketId}\") {{ cycles {{ id }} }} }}")));

        foreach (var (name, list) in counts)
        {
            Assert.True(list.Distinct().Count() == 1, $"{name}: query count changed with result size: {string.Join(',', list)}");
        }
    }

    private static void Record(Dictionary<string, List<int>> counts, string key, int value)
    {
        if (!counts.TryGetValue(key, out var list))
        {
            counts[key] = list = [];
        }

        list.Add(value);
    }

    // ---------------------------------------------------------------- exact aggregates at scale

    [Fact]
    public async Task Dashboard_and_report_match_independently_counted_seed_totals()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var seed = await BulkSeed.AddTicketsAsync(w, 1200, 1);
        var from = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).AddDays(-89);

        var dash = (await GqlAsync(w.Manager, w.Org.Id, $"{{ dashboard(filter: {{ from: \"{from:yyyy-MM-dd}\" }}) {{ current {{ openTickets unassignedTickets overdueTickets }} createdInPeriod resolvedInPeriod firstResponseSample avgFirstResponseMinutes sla {{ eligibleCycles met compliancePercent }} trend {{ created resolved }} workload {{ activeTickets }} }} }}"))
            .GetProperty("data").GetProperty("dashboard");
        Assert.Equal(seed.Open, dash.GetProperty("current").GetProperty("openTickets").GetInt32());
        Assert.Equal(seed.OpenUnassigned, dash.GetProperty("current").GetProperty("unassignedTickets").GetInt32());
        Assert.Equal(seed.OpenOverdue, dash.GetProperty("current").GetProperty("overdueTickets").GetInt32());
        Assert.Equal(seed.Tickets, dash.GetProperty("createdInPeriod").GetInt32());
        Assert.Equal(seed.CompletedCycles, dash.GetProperty("resolvedInPeriod").GetInt32());
        Assert.Equal(seed.Tickets, dash.GetProperty("trend").EnumerateArray().Sum(p => p.GetProperty("created").GetInt32())); // buckets lose nothing
        Assert.Equal(seed.CompletedCycles, dash.GetProperty("trend").EnumerateArray().Sum(p => p.GetProperty("resolved").GetInt32()));
        Assert.Equal(seed.CompletedCycles, dash.GetProperty("sla").GetProperty("eligibleCycles").GetInt32());
        Assert.Equal(seed.CompletedCyclesMet, dash.GetProperty("sla").GetProperty("met").GetInt32());
        Assert.True(dash.GetProperty("firstResponseSample").GetInt32() > 0);
        Assert.Equal(30.0, dash.GetProperty("avgFirstResponseMinutes").GetDouble(), 3);

        var report = (await GqlAsync(w.Manager, w.Org.Id, $"{{ report(filter: {{ from: \"{from:yyyy-MM-dd}\" }}) {{ ticketsCreated resolution {{ completedCycles avgMinutes medianMinutes }} reopen {{ resolvedCycles reopenedCycles reopenRatePercent }} openOverdueTickets categories {{ count sharePercent }} agents {{ currentWorkload }} }} }}"))
            .GetProperty("data").GetProperty("report");
        var sorted = seed.CompletedMinutes.Order().ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
        Assert.Equal(seed.Tickets, report.GetProperty("ticketsCreated").GetInt32());
        Assert.Equal(seed.CompletedCycles, report.GetProperty("resolution").GetProperty("completedCycles").GetInt32());
        Assert.Equal(seed.CompletedMinutesSum / seed.CompletedCycles, report.GetProperty("resolution").GetProperty("avgMinutes").GetDouble(), 3);
        Assert.Equal(median, report.GetProperty("resolution").GetProperty("medianMinutes").GetDouble(), 3);
        Assert.Equal(seed.ReopenedCycles, report.GetProperty("reopen").GetProperty("reopenedCycles").GetInt32());
        Assert.Equal(seed.CompletedCycles, report.GetProperty("reopen").GetProperty("resolvedCycles").GetInt32());
        Assert.Equal(seed.OpenOverdue, report.GetProperty("openOverdueTickets").GetInt32());
        Assert.Equal(seed.Tickets, report.GetProperty("categories").EnumerateArray().Sum(c => c.GetProperty("count").GetInt32()));
        // personal workloads: a ticket with two agents counts for each, so the sum exceeds the number of DISTINCT assigned open tickets
        var workloadSum = report.GetProperty("agents").EnumerateArray().Sum(x => x.GetProperty("currentWorkload").GetInt32());
        Assert.Equal(seed.OpenAssignments, workloadSum);
        Assert.True(workloadSum > seed.Open - seed.OpenUnassigned);
    }

    [Theory]
    [InlineData(new[] { 10, 20, 30 }, 20.0, 20.0)]
    [InlineData(new[] { 10, 20, 30, 40 }, 25.0, 25.0)]
    [InlineData(new[] { 15 }, 15.0, 15.0)]
    public async Task Average_and_median_are_exact_for_odd_even_and_single_samples(int[] minutes, double average, double median)
    {
        var w = await TicketWorld.CreateAsync(factory);
        var day = factory.Clock.GetUtcNow().UtcDateTime.AddHours(-12);
        foreach (var m in minutes)
        {
            await BulkSeed.AddSimpleTicketAsync(w, day, m);
        }

        var r = (await GqlAsync(w.Manager, w.Org.Id, "{ report { resolution { completedCycles avgMinutes medianMinutes } } }")).GetProperty("data").GetProperty("report").GetProperty("resolution");
        Assert.Equal(minutes.Length, r.GetProperty("completedCycles").GetInt32());
        Assert.Equal(average, r.GetProperty("avgMinutes").GetDouble(), 3);
        Assert.Equal(median, r.GetProperty("medianMinutes").GetDouble(), 3);
    }

    [Fact]
    public async Task Empty_population_gives_null_not_zero()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var r = (await GqlAsync(w.Manager, w.Org.Id, "{ report { resolution { completedCycles avgMinutes medianMinutes } reopen { reopenRatePercent } } }")).GetProperty("data").GetProperty("report");
        Assert.Equal(0, r.GetProperty("resolution").GetProperty("completedCycles").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("resolution").GetProperty("avgMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("resolution").GetProperty("medianMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("reopen").GetProperty("reopenRatePercent").ValueKind);
    }

    [Fact]
    public async Task Trend_has_a_zero_filled_bucket_for_every_day_in_order()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc));
        var days = (await GqlAsync(w.Manager, w.Org.Id, "{ report(filter: { from: \"2026-05-01\", to: \"2026-05-07\" }) { trend { periodStartUtc created } } }")).GetProperty("data").GetProperty("report").GetProperty("trend").EnumerateArray().ToList();
        Assert.Equal(7, days.Count);
        Assert.Equal([0, 0, 1, 0, 0, 0, 0], days.Select(d => d.GetProperty("created").GetInt32()).ToArray());
        Assert.Equal(days.Select(d => d.GetProperty("periodStartUtc").GetDateTime()).Order().ToList(), days.Select(d => d.GetProperty("periodStartUtc").GetDateTime()).ToList());
    }

    [Fact]
    public async Task Long_ranges_are_grouped_by_month()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 2, 10, 10, 0, 0, DateTimeKind.Utc));
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc));
        var r = (await GqlAsync(w.Manager, w.Org.Id, "{ report(filter: { from: \"2026-01-01\", to: \"2026-06-30\" }) { grouping trend { created } } }")).GetProperty("data").GetProperty("report");
        Assert.Equal("month", r.GetProperty("grouping").GetString());
        var counts = r.GetProperty("trend").EnumerateArray().Select(p => p.GetProperty("created").GetInt32()).ToArray();
        Assert.Equal([0, 2, 0, 0, 0, 0], counts);
    }
}

public class ExportScaleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Large_ticket_export_is_capped_at_the_limit_and_says_so()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddTicketsAsync(w, 10_050, 1);
        var from = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime).AddDays(-89);

        var response = await w.Manager.GetAsync(w.Url($"reports/export?kind=Tickets&from={from:yyyy-MM-dd}"));
        var lines = (await response.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("true", response.Headers.GetValues("X-Export-Truncated").Single());
        Assert.Equal(10_001, lines.Length); // header + exactly the cap, never silently more or fewer
        Assert.Equal(10_000, lines.Skip(1).Select(l => l.Split(',')[0]).Distinct().Count()); // deterministic, no duplicates
    }

    [Fact]
    public async Task Cancelled_requests_stop_dashboard_report_and_export_work()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using var scope = w.ScopeAs(w.Manager, w.ManagerMembership, OpsFlow.Domain.Tenants.TenantRole.Manager);
        TicketWorld.SignInAs(scope.ServiceProvider, w.Manager);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();
        var filter = new AnalyticsFilter(null, null, null, null, null, null, null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDashboardAsync(filter, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetReportAsync(filter, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(ReportExportKind.Tickets, filter, cts.Token));
    }
}

/// <summary>DST correctness end-to-end: the tenant reporting day is Europe/Rome, instants are stored in UTC.</summary>
public class RomeTimeZoneTests(RomeApiFactory factory) : IClassFixture<RomeApiFactory>
{
    [Fact]
    public async Task Tickets_near_local_midnight_fall_into_the_correct_local_day_across_the_dst_change()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 3, 28, 22, 30, 0, DateTimeKind.Utc)); // 23:30 CET on Mar 28
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 3, 28, 23, 30, 0, DateTimeKind.Utc)); // 00:30 CET on Mar 29 (23-hour day)
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 3, 29, 21, 30, 0, DateTimeKind.Utc)); // 23:30 CEST on Mar 29
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 3, 29, 22, 30, 0, DateTimeKind.Utc)); // 00:30 CEST on Mar 30

        var response = await w.Manager.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ report(filter: { from: \"2026-03-28\", to: \"2026-03-30\" }) { timeZone fromUtc toUtc ticketsCreated trend { periodStartUtc created } } }" });
        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("report");

        Assert.Equal("Europe/Rome", report.GetProperty("timeZone").GetString());
        Assert.Equal(new DateTime(2026, 3, 27, 23, 0, 0, DateTimeKind.Utc), report.GetProperty("fromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(new DateTime(2026, 3, 30, 22, 0, 0, DateTimeKind.Utc), report.GetProperty("toUtc").GetDateTime().ToUniversalTime()); // end exclusive, 71 hours later
        Assert.Equal(4, report.GetProperty("ticketsCreated").GetInt32());
        Assert.Equal([1, 2, 1], report.GetProperty("trend").EnumerateArray().Select(p => p.GetProperty("created").GetInt32()).ToArray());
    }
}
