using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpsFlow.Application.Analytics;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>GraphQL read layer (auth, tenant isolation, limits) plus dashboard/report metrics and CSV export.</summary>
public class GraphQlAnalyticsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> GqlAsync(ApiClient who, Guid tenantId, string query, object? variables = null)
    {
        var response = await who.PostAsync($"/api/v1/tenants/{tenantId}/graphql", new { query, variables });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string? FirstErrorCode(JsonElement body) =>
        body.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 && errors[0].TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code)
            ? code.GetString() : null;

    private static async Task<TicketJson> ResolvedTicketAsync(TicketWorld w, params Guid[] agents)
    {
        var t = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(t.Id, agents);
        await w.MoveAsync(w.Manager, t.Id, "InProgress");
        return await w.MoveAsync(w.Manager, t.Id, "Resolved");
    }

    [Fact]
    public async Task Unauthenticated_graphql_request_is_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ dashboard { timeZone } }" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_member_cannot_query_another_tenant()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var outsider = await ApiClient.RegisterAsync(factory);
        var response = await outsider.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ tickets { items { id } } }" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_sees_only_own_tickets_and_no_staff_data()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var mine = await w.CreateTicketAsync(w.Customer);
        var other = await w.CreateCustomerAsync(null, "Someone Else");
        await w.CreateTicketAsync(w.Owner, new { title = "Not yours", description = "x", categoryId = w.CategoryId, customerId = other.Id });

        var body = await GqlAsync(w.Customer, w.Org.Id, "{ tickets { totalCount items { id assigneeCount resolutionDueAtUtc } } }");
        var data = body.GetProperty("data").GetProperty("tickets");
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(mine.Id, data.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("items")[0].GetProperty("assigneeCount").ValueKind);

        Assert.Equal("FORBIDDEN", FirstErrorCode(await GqlAsync(w.Customer, w.Org.Id, "{ report { ticketsCreated } }")));
        Assert.Equal("FORBIDDEN", FirstErrorCode(await GqlAsync(w.Customer, w.Org.Id, "{ customers { items { id } } }")));
        Assert.Equal("FORBIDDEN", FirstErrorCode(await GqlAsync(w.Customer, w.Org.Id, $"{{ ticketSla(ticketId: \"{mine.Id}\") {{ cycles {{ id }} }} }}")));
    }

    [Fact]
    public async Task Cross_tenant_ticket_id_is_not_found()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var secret = await a.CreateTicketAsync(a.Customer);

        var body = await GqlAsync(b.Manager, b.Org.Id, $"{{ ticket(id: \"{secret.Id}\") {{ description }} }}");
        Assert.Equal("NOT_FOUND", FirstErrorCode(body));
        var list = await GqlAsync(b.Manager, b.Org.Id, "{ tickets { totalCount } }");
        Assert.Equal(0, list.GetProperty("data").GetProperty("tickets").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Agent_dashboard_is_limited_to_visible_tickets_and_has_no_tenant_workload()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(t.Id, w.Agent1Membership);
        await w.CreateTicketAsync(w.Customer); // not visible to Agent1

        var agent = (await GqlAsync(w.Agent1, w.Org.Id, "{ dashboard { current { openTickets } workload { displayName } } }")).GetProperty("data").GetProperty("dashboard");
        Assert.Equal(1, agent.GetProperty("current").GetProperty("openTickets").GetInt32());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("workload").ValueKind);
        var manager = (await GqlAsync(w.Manager, w.Org.Id, "{ dashboard { current { openTickets unassignedTickets } workload { activeTickets } } }")).GetProperty("data").GetProperty("dashboard");
        Assert.Equal(2, manager.GetProperty("current").GetProperty("openTickets").GetInt32());
        Assert.Equal(1, manager.GetProperty("current").GetProperty("unassignedTickets").GetInt32());
        Assert.Equal(1, manager.GetProperty("workload").EnumerateArray().Sum(x => x.GetProperty("activeTickets").GetInt32())); // one assignment, counted per agent
    }

    [Fact]
    public async Task Too_deep_queries_are_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var deep = "{ tickets { items { " + string.Concat(Enumerable.Repeat("x { ", 15)) + "id" + string.Concat(Enumerable.Repeat(" }", 15)) + " } } }";
        var body = await GqlAsync(w.Manager, w.Org.Id, deep);
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Introspection_is_disabled_outside_development()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var body = await GqlAsync(w.Manager, w.Org.Id, "{ __schema { types { name } } }");
        Assert.True(body.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Resolved_reopened_cycles_drive_sla_resolution_and_reopen_metrics()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await ResolvedTicketAsync(w, w.Agent1Membership);
        var reopened = await w.Customer.ReadAsync<TicketJson>(await w.Customer.PostAsync(w.Url($"tickets/{ticket.Id}/reopen"), new { }));
        Assert.Equal("Assigned", reopened.Status);
        await ResolvedTicketAsync(w, w.Agent2Membership);

        var report = (await GqlAsync(w.Manager, w.Org.Id,
            "{ report { ticketsCreated resolution { completedCycles } sla { eligibleCycles met breached compliancePercent } reopen { resolvedCycles reopenedCycles reopenRatePercent reopenEventsInPeriod } agents { displayName resolvedInPeriod } } }"))
            .GetProperty("data").GetProperty("report");

        Assert.Equal(2, report.GetProperty("ticketsCreated").GetInt32());
        Assert.Equal(2, report.GetProperty("resolution").GetProperty("completedCycles").GetInt32()); // reopened ticket is still open: its 2nd cycle is unfinished
        Assert.Equal(2, report.GetProperty("sla").GetProperty("met").GetInt32());
        Assert.Equal(100.0, report.GetProperty("sla").GetProperty("compliancePercent").GetDouble());
        Assert.Equal(1, report.GetProperty("reopen").GetProperty("reopenedCycles").GetInt32());
        Assert.Equal(50.0, report.GetProperty("reopen").GetProperty("reopenRatePercent").GetDouble());
        Assert.Equal(1, report.GetProperty("reopen").GetProperty("reopenEventsInPeriod").GetInt32());
        // attribution = who performed the Resolve transition (the manager), never the current assignee
        Assert.All(report.GetProperty("agents").EnumerateArray(), a => Assert.Equal(0, a.GetProperty("resolvedInPeriod").GetInt32()));
    }

    [Fact]
    public async Task Empty_sample_yields_null_instead_of_fake_percentages()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var dash = (await GqlAsync(w.Manager, w.Org.Id, "{ dashboard { avgFirstResponseMinutes sla { compliancePercent } current { resolvedToday } } }")).GetProperty("data").GetProperty("dashboard");
        Assert.Equal(JsonValueKind.Null, dash.GetProperty("avgFirstResponseMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, dash.GetProperty("sla").GetProperty("compliancePercent").ValueKind);
        Assert.Equal(0, dash.GetProperty("current").GetProperty("resolvedToday").GetInt32());
    }

    [Fact]
    public async Task Invalid_date_ranges_are_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var reversed = await GqlAsync(w.Manager, w.Org.Id, "{ report(filter: { from: \"2026-02-10\", to: \"2026-02-01\" }) { ticketsCreated } }");
        var tooLong = await GqlAsync(w.Manager, w.Org.Id, "{ report(filter: { from: \"2024-01-01\", to: \"2026-01-01\" }) { ticketsCreated } }");
        Assert.Equal("VALIDATION_ERROR", FirstErrorCode(reversed));
        Assert.Equal("VALIDATION_ERROR", FirstErrorCode(tooLong));
    }

    [Fact]
    public async Task Kanban_columns_follow_the_real_statuses_and_are_bounded()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var t = await w.CreateTicketAsync(w.Customer);
        await w.AssignAsync(t.Id, w.Agent1Membership);
        var board = (await GqlAsync(w.Manager, w.Org.Id, "{ kanbanBoard(perColumn: 1) { status total tickets { id } } }")).GetProperty("data").GetProperty("kanbanBoard");
        Assert.Equal(["OPEN", "ASSIGNED", "IN_PROGRESS", "ON_HOLD", "RESOLVED"], board.EnumerateArray().Select(c => c.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(1, board.EnumerateArray().Single(c => c.GetProperty("status").GetString() == "ASSIGNED").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Csv_export_is_manager_only_filtered_and_formula_safe()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Customer, new { title = "=HYPERLINK(\"http://evil\"),\"quoted\"", description = "d", categoryId = w.CategoryId });
        var other = await TicketWorld.CreateAsync(factory);
        await other.CreateTicketAsync(other.Customer, new { title = "OtherTenantSecret", description = "d", categoryId = other.CategoryId });

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Agent1.GetAsync(w.Url("reports/export"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url("reports/export"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.GetAsync(w.Url("reports/export?from=2026-02-10&to=2026-02-01"))).StatusCode);

        var ok = await w.Manager.GetAsync(w.Url("reports/export?kind=Tickets"));
        var csv = await ok.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("text/csv", ok.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\"),\"\"quoted\"\"\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("OtherTenantSecret", csv, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, (await ok.Content.ReadAsByteArrayAsync()).Take(3).ToArray()); // UTF-8 BOM for Excel
        Assert.StartsWith("ticket,title,status", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-5", "'-5")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_cells_are_escaped(string input, string expected) => Assert.Equal(expected, CsvWriter.Cell(input));
}
