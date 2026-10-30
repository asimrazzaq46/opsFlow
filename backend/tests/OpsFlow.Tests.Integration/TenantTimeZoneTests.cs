using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpsFlow.Application.Analytics;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Database-backed tenant reporting timezone: settings API, authorization, isolation, consistent use by reports/CSV, EF mapping.</summary>
public class TenantTimeZoneTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> ReportAsync(ApiClient who, Guid tenantId, string from, string to)
    {
        var query = $"{{ report(filter: {{ from: \"{from}\", to: \"{to}\" }}) {{ timeZone fromUtc toUtc ticketsCreated trend {{ periodStartUtc created }} }} }}";
        var response = await who.PostAsync($"/api/v1/tenants/{tenantId}/graphql", new { query });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("report").Clone();
    }

    private static async Task SetZoneAsync(TicketWorld w, string zone) =>
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = zone })).StatusCode);

    private async Task<string> StoredZoneAsync(TicketWorld w)
    {
        using var scope = w.ScopeAs(w.Owner, w.OwnerMembership, TenantRole.TenantOwner);
        return await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Settings.AsNoTracking().Select(s => s.ReportingTimeZone).SingleAsync();
    }

    // ---------------------------------------------------------------- API + authorization

    [Fact]
    public async Task New_and_legacy_tenants_report_in_utc_and_staff_can_read_the_setting()
    {
        var w = await TicketWorld.CreateAsync(factory);
        foreach (var staff in new[] { w.Owner, w.Manager, w.Agent1 })
        {
            var body = await staff.ReadAsync<JsonElement>(await staff.GetAsync(w.Url("settings")));
            Assert.Equal("UTC", body.GetProperty("reportingTimeZone").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await w.Customer.GetAsync(w.Url("settings"))).StatusCode);
        Assert.Equal("UTC", (await ReportAsync(w.Manager, w.Org.Id, "2026-03-01", "2026-03-02")).GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Legacy_tenant_without_a_settings_row_falls_back_to_utc_and_cannot_be_updated_until_initialized()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using (var scope = w.ScopeAs(w.Owner, w.OwnerMembership, TenantRole.TenantOwner))
        {
            await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Settings.ExecuteDeleteAsync();
        }

        Assert.Equal("UTC", (await w.Owner.ReadAsync<JsonElement>(await w.Owner.GetAsync(w.Url("settings")))).GetProperty("reportingTimeZone").GetString());
        Assert.Equal("UTC", (await ReportAsync(w.Manager, w.Org.Id, "2026-03-01", "2026-03-02")).GetProperty("timeZone").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = "Europe/Rome" })).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_can_change_the_timezone()
    {
        var w = await TicketWorld.CreateAsync(factory);

        foreach (var other in new[] { w.Manager, w.Agent1, w.Customer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync(w.Url("settings"), new { reportingTimeZone = "Europe/Rome" })).StatusCode);
        }

        Assert.Equal("UTC", await StoredZoneAsync(w));
        await SetZoneAsync(w, "Europe/Rome");
        Assert.Equal("Europe/Rome", await StoredZoneAsync(w));
        Assert.Equal("Europe/Rome", (await w.Agent1.ReadAsync<JsonElement>(await w.Agent1.GetAsync(w.Url("settings")))).GetProperty("reportingTimeZone").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("europe/rome")]
    [InlineData("Europe/Rome; DROP TABLE Tickets")]
    [InlineData("UTC+5")]
    public async Task Invalid_timezone_ids_are_rejected_and_nothing_is_stored(string zone)
    {
        var w = await TicketWorld.CreateAsync(factory);
        var response = await w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = zone });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("UTC", await StoredZoneAsync(w));
    }

    [Fact]
    public async Task Too_long_and_missing_values_are_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = new string('a', 65) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PutAsync(w.Url("settings"), new { })).StatusCode);
    }

    [Fact]
    public async Task One_tenant_cannot_read_or_change_another_tenants_timezone()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        await SetZoneAsync(a, "Asia/Karachi");

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.PutAsync(a.Url("settings"), new { reportingTimeZone = "Europe/Rome" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url("settings"))).StatusCode);
        Assert.Equal("Asia/Karachi", await StoredZoneAsync(a));
        Assert.Equal("UTC", await StoredZoneAsync(b));
        Assert.Equal("UTC", (await ReportAsync(b.Manager, b.Org.Id, "2026-03-01", "2026-03-02")).GetProperty("timeZone").GetString());
        Assert.Equal("Asia/Karachi", (await ReportAsync(a.Manager, a.Org.Id, "2026-03-01", "2026-03-02")).GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Concurrent_updates_end_in_one_of_the_submitted_values()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var calls = await Task.WhenAll(
            w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = "Europe/Rome" }),
            w.Owner.PutAsync(w.Url("settings"), new { reportingTimeZone = "Asia/Karachi" }));

        Assert.All(calls, c => Assert.True(c.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, c.StatusCode.ToString()));
        Assert.True(await StoredZoneAsync(w) is "Europe/Rome" or "Asia/Karachi");
    }

    // ---------------------------------------------------------------- reporting / CSV use the tenant zone

    [Fact]
    public async Task Europe_rome_buckets_resolve_dst_start_midnight_and_match_the_csv()
    {
        var w = await TicketWorld.CreateAsync(factory);
        foreach (var utc in new[] { new DateTime(2026, 3, 28, 22, 30, 0), new DateTime(2026, 3, 28, 23, 30, 0), new DateTime(2026, 3, 29, 21, 30, 0), new DateTime(2026, 3, 29, 22, 30, 0) })
        {
            await BulkSeed.AddSimpleTicketAsync(w, DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }

        await SetZoneAsync(w, "Europe/Rome");

        var report = await ReportAsync(w.Manager, w.Org.Id, "2026-03-29", "2026-03-29");
        Assert.Equal("Europe/Rome", report.GetProperty("timeZone").GetString());
        Assert.Equal(new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc), report.GetProperty("fromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc), report.GetProperty("toUtc").GetDateTime().ToUniversalTime()); // 23-hour day, end exclusive
        Assert.Equal(2, report.GetProperty("ticketsCreated").GetInt32());

        var csv = await w.Manager.GetAsync(w.Url("reports/export?kind=Tickets&from=2026-03-29&to=2026-03-29"));
        var rows = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 2, rows.Length); // identical boundaries as the report
        Assert.Contains("20260329-20260329", csv.Content.Headers.ContentDisposition!.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Europe_rome_dst_end_day_has_25_hours()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await SetZoneAsync(w, "Europe/Rome");
        var report = await ReportAsync(w.Manager, w.Org.Id, "2026-10-25", "2026-10-25");
        Assert.Equal(25, (report.GetProperty("toUtc").GetDateTime() - report.GetProperty("fromUtc").GetDateTime()).TotalHours);
    }

    [Fact]
    public async Task Karachi_and_year_boundaries_use_the_local_calendar()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 5, 31, 19, 30, 0, DateTimeKind.Utc)); // 00:30 on June 1 in Karachi (UTC+5)
        await BulkSeed.AddSimpleTicketAsync(w, new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc)); // 00:30 on Jan 1 2027 in Rome

        Assert.Equal(0, (await ReportAsync(w.Manager, w.Org.Id, "2026-06-01", "2026-06-01")).GetProperty("ticketsCreated").GetInt32());
        await SetZoneAsync(w, "Asia/Karachi");
        Assert.Equal(1, (await ReportAsync(w.Manager, w.Org.Id, "2026-06-01", "2026-06-01")).GetProperty("ticketsCreated").GetInt32());

        await SetZoneAsync(w, "Europe/Rome");
        Assert.Equal(1, (await ReportAsync(w.Manager, w.Org.Id, "2027-01-01", "2027-01-01")).GetProperty("ticketsCreated").GetInt32());
        Assert.Equal(0, (await ReportAsync(w.Manager, w.Org.Id, "2026-12-31", "2026-12-31")).GetProperty("ticketsCreated").GetInt32());
    }

    [Fact]
    public async Task Resolved_today_uses_the_tenant_local_day_start_inclusive()
    {
        var w = await TicketWorld.CreateAsync(factory);
        const string zone = "Pacific/Kiritimati"; // UTC+14, no DST: its "today" starts at a UTC time very different from UTC midnight
        await SetZoneAsync(w, zone);
        var tz = ReportingCalendar.Resolve(zone);
        var now = factory.Clock.GetUtcNow().UtcDateTime;
        var localStart = ReportingCalendar.LocalDayStartUtc(ReportingCalendar.LocalDate(now, tz), tz);
        await BulkSeed.AddSimpleTicketAsync(w, localStart.AddHours(-3), 180); // resolved exactly at local midnight: counts today
        await BulkSeed.AddSimpleTicketAsync(w, localStart.AddHours(-3), 179); // one minute earlier: yesterday

        var body = await w.Manager.PostAsync($"/api/v1/tenants/{w.Org.Id}/graphql", new { query = "{ dashboard { timeZone current { resolvedToday } } }" });
        var dash = JsonDocument.Parse(await body.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("dashboard");
        Assert.Equal(zone, dash.GetProperty("timeZone").GetString());
        Assert.Equal(1, dash.GetProperty("current").GetProperty("resolvedToday").GetInt32());
    }

    [Fact]
    public async Task An_unusable_stored_timezone_falls_back_to_utc_instead_of_breaking_reports()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using (var scope = w.ScopeAs(w.Owner, w.OwnerMembership, TenantRole.TenantOwner))
        {
            await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportingTimeZone, "Bad/Zone"));
        }

        Assert.Equal("UTC", (await ReportAsync(w.Manager, w.Org.Id, "2026-03-01", "2026-03-02")).GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task The_timezone_is_read_once_per_request_scope()
    {
        var w = await TicketWorld.CreateAsync(factory);
        using var scope = w.ScopeAs(w.Owner, w.OwnerMembership, TenantRole.TenantOwner);
        var provider = scope.ServiceProvider.GetRequiredService<IReportingTimeZoneProvider>();

        var commands = await factory.Queries.MeasureAsync(async () =>
        {
            await provider.GetAsync(CancellationToken.None);
            await provider.GetAsync(CancellationToken.None);
            await provider.GetAsync(CancellationToken.None);
        });

        Assert.Equal(1, commands);
    }

    // ---------------------------------------------------------------- EF mapping / index

    [Fact]
    public void Ef_model_maps_the_timezone_column_and_the_ticket_ordering_index()
    {
        using var scope = factory.Services.CreateScope();
        var model = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>()).Model;

        var property = model.FindEntityType(typeof(TenantSettings))!.FindProperty(nameof(TenantSettings.ReportingTimeZone))!;
        Assert.False(property.IsNullable);
        Assert.Equal(64, property.GetMaxLength());
        Assert.Equal("UTC", property.GetDefaultValue());

        var index = model.FindEntityType(typeof(Ticket))!.GetIndexes().Single(i => i.GetDatabaseName() == "IX_Tickets_TenantId_UpdatedAtUtc_Number");
        Assert.Equal([nameof(Ticket.TenantId), nameof(Ticket.UpdatedAtUtc), nameof(Ticket.Number)], index.Properties.Select(p => p.Name).ToArray());
        Assert.Equal([false, true, false], index.IsDescending!.ToArray());
        Assert.False(index.IsUnique);
    }

    [PostgresOnlyFact]
    public async Task Postgres_schema_has_a_varchar_64_default_utc_column_and_the_descending_index()
    {
        _ = factory.Services; // the host (and EnsureCreated) starts lazily
        await using var connection = new NpgsqlConnection(factory.PostgresConnectionString);
        await connection.OpenAsync();

        await using var column = new NpgsqlCommand("SELECT data_type, character_maximum_length, is_nullable, column_default FROM information_schema.columns WHERE table_name = 'TenantSettings' AND column_name = 'ReportingTimeZone'", connection);
        await using (var reader = await column.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("character varying", reader.GetString(0));
            Assert.Equal(64, reader.GetInt32(1));
            Assert.Equal("NO", reader.GetString(2));
            Assert.Contains("UTC", reader.GetString(3), StringComparison.Ordinal);
        }

        await using var index = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_Tickets_TenantId_UpdatedAtUtc_Number'", connection);
        var definition = (string?)await index.ExecuteScalarAsync();
        Assert.NotNull(definition);
        Assert.Contains("(\"TenantId\", \"UpdatedAtUtc\" DESC, \"Number\")", definition, StringComparison.Ordinal);
    }
}
