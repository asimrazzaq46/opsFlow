using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Options;
using Npgsql;
using OpsFlow.Infrastructure.Background;

namespace OpsFlow.Api.Infrastructure.Background;

public sealed class HangfireOptions
{
    public const string SectionName = "Hangfire";

    /// <summary>Empty = reuse ConnectionStrings:Default. A DEDICATED role/database is recommended in production.</summary>
    public string? ConnectionString { get; set; }

    public string SchemaName { get; set; } = "hangfire";

    /// <summary>
    /// Lets the Hangfire PostgreSQL provider CREATE its own schema on startup. Off by default: infrastructure DDL against an existing database
    /// needs an explicit decision (see docs/background-jobs.md). When off, startup fails with a clear message if the schema is missing.
    /// </summary>
    public bool PrepareSchema { get; set; }

    public bool DashboardEnabled { get; set; }

    /// <summary>Required in addition to DashboardEnabled outside Development. The dashboard is still loopback-only and read-only there.</summary>
    public bool DashboardAllowInProduction { get; set; }

    public string DashboardPath { get; set; } = "/hangfire";
}

public static class BackgroundSetup
{
    /// <summary>Registers Hangfire (storage + worker + recurring jobs) ONLY when <c>Background:ServerEnabled</c> is true.</summary>
    public static IServiceCollection AddOpsFlowBackground(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HangfireOptions>(configuration.GetSection(HangfireOptions.SectionName));
        var background = configuration.GetSection(BackgroundOptions.SectionName).Get<BackgroundOptions>() ?? new BackgroundOptions();
        if (!background.ServerEnabled)
        {
            services.AddHostedService<BackgroundDisabledNotice>(); // UPDATED: disabled worker ab startup par saaf dikhta hai (chup nahi)
            return services;
        }

        var hangfire = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions();
        var connectionString = hangfire.ConnectionString is { Length: > 0 } own ? own : configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Hangfire needs Hangfire:ConnectionString or ConnectionStrings:Default.");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
            {
                SchemaName = hangfire.SchemaName,
                PrepareSchemaIfNecessary = hangfire.PrepareSchema,
                QueuePollInterval = TimeSpan.FromSeconds(2),
            }));

        // Registration order = start order: the storage check must run before the worker starts polling.
        services.AddHostedService<HangfireStartupCheck>();
        services.AddHangfireServer(o =>
        {
            o.WorkerCount = Math.Clamp(background.WorkerCount, 1, 32);
            o.SchedulePollingInterval = TimeSpan.FromSeconds(Math.Clamp(background.SchedulePollingSeconds, 1, 60));
            o.ServerName = $"opsflow-{Environment.MachineName}-{Environment.ProcessId}";
        });
        services.AddHostedService<RecurringJobsRegistrar>();
        return services;
    }

    /// <summary>Dashboard: off unless enabled; in production additionally needs an explicit opt-in. Never reachable from a remote client.</summary>
    public static IApplicationBuilder UseOpsFlowHangfireDashboard(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<HangfireOptions>>().Value;
        var background = app.Services.GetRequiredService<IOptions<BackgroundOptions>>().Value;
        if (!background.ServerEnabled || !options.DashboardEnabled)
        {
            return app;
        }

        var development = app.Environment.IsDevelopment();
        if (!development && !options.DashboardAllowInProduction)
        {
            BackgroundLog.DashboardIgnored(app.Logger);
            return app;
        }

        return app.UseHangfireDashboard(options.DashboardPath, new DashboardOptions
        {
            Authorization = [new LoopbackOnlyDashboardFilter()],
            IsReadOnlyFunc = _ => !development, // production: observe only, no requeue/delete from the dashboard
            DashboardTitle = "OpsFlow background jobs",
        });
    }
}

internal static partial class BackgroundLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "BACKGROUND WORKER IS DISABLED (Background:ServerEnabled=false) in environment {Environment}: outbox delivery will NOT run (no outbox-based SignalR live events, no background e-mails, no cache-invalidation retries) and SLA monitoring will NOT run. Set Background:ServerEnabled=true on at least one instance (see docs/background-jobs.md).")]
    public static partial void WorkerDisabled(ILogger logger, string environment);

    [LoggerMessage(Level = LogLevel.Information, Message = "Background worker intentionally disabled in the {Environment} environment (tests/tooling); outbox and SLA jobs do not run.")]
    public static partial void WorkerDisabledForTests(ILogger logger, string environment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hangfire:DashboardEnabled is ignored outside Development unless Hangfire:DashboardAllowInProduction is true.")]
    public static partial void DashboardIgnored(ILogger logger);
}

/// <summary>
/// The dashboard shows internal data of EVERY tenant, so tenant users are never admitted, whatever their role: only requests that reach
/// the process directly from the loopback interface (SSH tunnel / local machine) and carry no proxy-forwarding headers.
/// </summary>
internal sealed class LoopbackOnlyDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        return http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
            && !http.Request.Headers.ContainsKey("X-Forwarded-For") && !http.Request.Headers.ContainsKey("Forwarded");
    }
}

/// <summary>Fails the host start with an actionable message when the Hangfire storage is unreachable or its schema is missing (never run jobs without persistence).</summary>
internal sealed class HangfireStartupCheck(IConfiguration configuration, IOptions<HangfireOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var connectionString = o.ConnectionString is { Length: > 0 } own ? own : configuration.GetConnectionString("Default")!;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            if (o.PrepareSchema)
            {
                return; // the storage provider creates its schema itself (explicitly enabled)
            }

            await using var command = new NpgsqlCommand("SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'schema'", connection);
            command.Parameters.AddWithValue("schema", o.SchemaName);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException($"Hangfire schema '{o.SchemaName}' does not exist. Initialize it once (set Hangfire:PrepareSchema=true for the first start with a role that may CREATE SCHEMA, or run the provider's install script), or set Background:ServerEnabled=false to run WITHOUT the worker (no outbox delivery, live events, background e-mails or SLA monitoring) - see docs/background-jobs.md.");
            }
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException($"Hangfire PostgreSQL storage is unreachable ({ex.GetType().Name}). Background jobs are NOT started without persistent storage; check Hangfire:ConnectionString / ConnectionStrings:Default.", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Idempotent add-or-update of the recurring jobs under stable ids (several instances starting together converge on the same schedule).</summary>
internal sealed class RecurringJobsRegistrar(IRecurringJobManager manager, IOptions<BackgroundOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var utc = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        manager.AddOrUpdate<OutboxDispatchJob>(BackgroundJobIds.OutboxDispatch, j => j.RunAsync(CancellationToken.None), o.OutboxDispatchCron, utc);
        manager.AddOrUpdate<SlaScanJob>(BackgroundJobIds.SlaScan, j => j.RunAsync(CancellationToken.None), o.SlaScanCron, utc);
        manager.AddOrUpdate<OutboxMaintenanceJob>(BackgroundJobIds.OutboxMaintenance, j => j.RunAsync(CancellationToken.None), o.OutboxMaintenanceCron, utc);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Makes a disabled worker visible: a warning normally (a production or development host without worker silently loses live events, e-mails and SLA automation), informational only for the Testing environment.</summary>
internal sealed class BackgroundDisabledNotice(IHostEnvironment environment, ILogger<BackgroundDisabledNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsEnvironment("Testing"))
        {
            BackgroundLog.WorkerDisabledForTests(logger, environment.EnvironmentName);
        }
        else
        {
            BackgroundLog.WorkerDisabled(logger, environment.EnvironmentName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
