using System.Net;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpsFlow.Domain.Outbox;
using OpsFlow.Infrastructure.Background;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Host with the Hangfire worker ENABLED against the disposable PostgreSQL test database (never the developer database).</summary>
public sealed class BackgroundApiFactory(bool prepareSchema, bool dashboard = false) : ApiFactory
{
    // a separate database: with Hangfire tables in the app database EnsureCreated() would consider the app schema "already there"
    public string HangfireConnectionString { get; } = TestDatabase.CreatePostgresDatabase($"opsflow_hf_{Guid.NewGuid():N}");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            TestDatabase.DropPostgresDatabase(new NpgsqlConnectionStringBuilder(HangfireConnectionString).Database!);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // UseSetting (not ConfigureAppConfiguration): Program.cs reads these keys while the services are being registered
        builder.UseSetting("Background:ServerEnabled", "true");
        builder.UseSetting("Background:WorkerCount", "2");
        builder.UseSetting("Background:SchedulePollingSeconds", "1");
        builder.UseSetting("Background:OutboxDispatchCron", "* * * * * *"); // every second, so the end-to-end test does not wait
        builder.UseSetting("Hangfire:ConnectionString", HangfireConnectionString);
        builder.UseSetting("Hangfire:PrepareSchema", prepareSchema ? "true" : "false");
        builder.UseSetting("Hangfire:DashboardEnabled", dashboard ? "true" : "false");
    }
}

public class HangfireTests
{
    private static readonly string[] JobIds = [BackgroundJobIds.OutboxDispatch, BackgroundJobIds.SlaScan, BackgroundJobIds.OutboxMaintenance];

    [Fact]
    public void Hangfire_is_not_registered_unless_the_worker_is_explicitly_enabled()
    {
        using var factory = new ApiFactory();
        _ = factory.CreateClient();

        Assert.Null(factory.Services.GetService<IBackgroundJobClient>());
    }

    [PostgresOnlyFact]
    public async Task Recurring_jobs_use_stable_ids_persistent_storage_and_a_dedicated_schema()
    {
        using var factory = new BackgroundApiFactory(prepareSchema: true);
        _ = factory.CreateClient(); // starts the host: storage check, worker, registrar

        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var jobs = connection.GetRecurringJobs().ToDictionary(j => j.Id);
        Assert.All(JobIds, id => Assert.Contains(id, jobs.Keys));
        Assert.All(jobs.Values, j => Assert.Equal("UTC", j.TimeZoneId));

        await using var db = new NpgsqlConnection(factory.HangfireConnectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'hangfire' AND table_name IN ('job','hash','set')", db);
        Assert.Equal(3L, await cmd.ExecuteScalarAsync()); // Hangfire lives in its own schema, apart from the EF tables
    }

    [PostgresOnlyFact]
    public void A_missing_hangfire_schema_stops_the_host_with_an_actionable_message_instead_of_running_jobs_unpersisted()
    {
        using var factory = new BackgroundApiFactory(prepareSchema: false);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("docs/background-jobs.md", Flatten(ex), StringComparison.Ordinal);
    }

    [PostgresOnlyFact]
    public async Task The_worker_processes_committed_outbox_events_without_any_http_request_involved()
    {
        using var factory = new BackgroundApiFactory(prepareSchema: true);
        var w = await TicketWorld.CreateAsync(factory);
        await w.CreateTicketAsync(w.Customer);

        var done = false;
        for (var i = 0; i < 120 && !done; i++) // up to 60 s: the suite runs many hosts in parallel
        {
            await Task.Delay(500);
            var rows = await factory.OutboxAsync(w.Org.Id);
            done = rows.Count > 0 && rows.All(m => m.Status == OutboxStatus.Completed);
        }

        Assert.True(done, "The Hangfire worker did not complete the outbox events in time.");
    }

    [PostgresOnlyFact]
    public async Task The_dashboard_is_not_served_by_default_and_denies_non_loopback_requests_when_enabled()
    {
        using var disabled = new BackgroundApiFactory(prepareSchema: true);
        var off = await disabled.CreateClient().GetAsync("/hangfire");
        Assert.NotEqual(HttpStatusCode.OK, off.StatusCode);
        Assert.DoesNotContain("Hangfire", await off.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var enabled = new BackgroundApiFactory(prepareSchema: true, dashboard: true);
        var status = (await enabled.CreateClient().GetAsync("/hangfire")).StatusCode; // TestServer requests have no loopback remote address
        Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Dashboard answered {status}");
    }

    private static string Flatten(Exception ex) => string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
        }
    }
}
