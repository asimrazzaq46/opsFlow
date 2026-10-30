using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

/// <summary>
/// Development startup flow: validate configuration and PostgreSQL connectivity, VERIFY (never change) the schema state, run the seeders.
/// Migrations are manual-only: model changes without a migration, or a migration that is not applied yet, stop the host before it serves
/// a single request. Nothing is migrated, created or reset here.
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

        // UPDATED: pehle sirf warning + skip tha aur MigrateAsync khud apply karta tha; ab verify-only aur fail-fast
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        try
        {
            DevelopmentMigrationGuard.EnsureUpToDate(db.Database.HasPendingModelChanges(), pending);
        }
        catch (InvalidOperationException ex)
        {
            LogSchemaBlocked(logger, target.Host, target.Port, target.Database, ex.Message);
            throw;
        }

        LogUpToDate(logger, target.Host, target.Port, target.Database);
        foreach (var seeder in scope.ServiceProvider.GetServices<IDevelopmentSeeder>())
        {
            await seeder.SeedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Development startup BLOCKED for {Host}:{Port}/{Database}; the API will not start. {Reason}")]
    private static partial void LogSchemaBlocked(ILogger logger, string? host, int port, string? database, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {Host}:{Port}/{Database} matches the model and all migrations are applied; running development seeders.")]
    private static partial void LogUpToDate(ILogger logger, string? host, int port, string? database);
}

/// <summary>The manual-migration policy as a pure decision, so every case is testable without a database.</summary>
public static class DevelopmentMigrationGuard
{
    private const string Tooling = "-p src/OpsFlow.Infrastructure -s src/OpsFlow.Api";

    public static void EnsureUpToDate(bool hasModelChanges, IReadOnlyCollection<string> pendingMigrations)
    {
        var problems = new List<string>();
        if (hasModelChanges)
        {
            problems.Add($"The EF model has changes that no migration covers. Generate and REVIEW a migration (from the backend folder): dotnet ef migrations add <Name> {Tooling} -o Persistence/Migrations");
        }

        if (pendingMigrations.Count > 0)
        {
            problems.Add($"{pendingMigrations.Count} migration(s) are not applied: {string.Join(", ", pendingMigrations)}. Apply them yourself: dotnet ef database update {Tooling}");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', problems) + " Startup does not migrate automatically.");
        }
    }
}
