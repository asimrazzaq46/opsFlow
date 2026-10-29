using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

/// <summary>
/// Development startup flow: validate configuration and PostgreSQL connectivity, apply the EXISTING migrations,
/// run the seeders. Runs before the server accepts requests; any failure stops startup with a clear message.
/// </summary>
public sealed partial class DevelopmentInitializer(IServiceScopeFactory scopes, ILogger<DevelopmentInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        var connectionString = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
        }

        var target = new NpgsqlConnectionStringBuilder(connectionString); // never log the password
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not reachable at {target.Host}:{target.Port}/{target.Database}. Start the local infrastructure with 'docker compose up -d' and check the user-secrets connection string.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        LogMigrated(logger, target.Host, target.Port, target.Database, pending.Count);

        foreach (var seeder in scope.ServiceProvider.GetServices<IDevelopmentSeeder>())
        {
            await seeder.SeedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {Host}:{Port}/{Database} is up to date ({PendingApplied} migration(s) applied).")]
    private static partial void LogMigrated(ILogger logger, string? host, int port, string? database, int pendingApplied);
}
