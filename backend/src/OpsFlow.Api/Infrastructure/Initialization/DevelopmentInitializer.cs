using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

/// <summary>
/// Development startup flow: validate configuration and PostgreSQL connectivity, reject model drift, optionally APPLY the existing pending
/// migrations (only in Development AND with Database:AutoApplyMigrationsOnStartup), VERIFY the result, then run the seeders.
/// Migrations are never generated here; model changes without a migration, a failed migration or a migration that is still not applied stop
/// the host before it serves a single request. Nothing is created or reset (no EnsureCreated/EnsureDeleted).
/// </summary>
public sealed class DevelopmentInitializer(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IOptions<DatabaseStartupOptions> database,
    ILogger<DevelopmentInitializer> logger
) : IHostedService
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

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var target = $"{builder.Host}:{builder.Port}/{builder.Database}"; // never log the password
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not reachable at {target}. Start the local infrastructure with 'docker compose up -d' and check the user-secrets connection string."
            );
        }

        // UPDATED: pehle sirf verify tha; ab model drift -> (sirf Development + explicit flag) existing migrations apply -> verify, phir seeders
        var autoApply = DevelopmentMigrationGuard.ShouldAutoApply(
            environment,
            database.Value.AutoApplyMigrationsOnStartup
        );
        await DevelopmentSchemaStep.EnsureReadyAsync(
            new EfSchemaState(db.Database),
            autoApply,
            logger,
            target,
            cancellationToken
        );

        foreach (var seeder in scope.ServiceProvider.GetServices<IDevelopmentSeeder>())
        {
            await seeder.SeedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The three schema facts/actions the startup step needs (a seam so every policy branch is testable without a database).</summary>
public interface ISchemaState
{
    Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken);

    bool HasModelChanges();

    Task MigrateAsync(CancellationToken cancellationToken);
}

internal sealed class EfSchemaState(
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database
) : ISchemaState
{
    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(
        CancellationToken cancellationToken
    ) => (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();

    public bool HasModelChanges() => database.HasPendingModelChanges();

    // EF's own MigrateAsync: its transaction/locking handling and its migration safety warnings stay untouched.
    public Task MigrateAsync(CancellationToken cancellationToken) =>
        database.MigrateAsync(cancellationToken);
}

public static partial class DevelopmentSchemaStep
{
    public static async Task EnsureReadyAsync(
        ISchemaState schema,
        bool autoApply,
        ILogger logger,
        string target,
        CancellationToken cancellationToken
    )
    {
        var pending = await schema.GetPendingMigrationsAsync(cancellationToken);
        try
        {
            var hasModelChanges = schema.HasModelChanges(); // drift is checked BEFORE anything is applied
            if (autoApply && pending.Count > 0 && !hasModelChanges)
            {
                var names = string.Join(", ", pending);
                LogApplying(logger, target, names);
                try
                {
                    await schema.MigrateAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    LogMigrationFailed(logger, ex, target);
                    throw;
                }

                pending = await schema.GetPendingMigrationsAsync(cancellationToken); // verify what is left
                LogApplied(logger, target, names);
            }

            DevelopmentMigrationGuard.EnsureUpToDate(hasModelChanges, pending, autoApply);
        }
        catch (InvalidOperationException ex)
        {
            LogSchemaBlocked(logger, target, ex.Message);
            throw;
        }

        LogUpToDate(logger, target);
    }

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Development startup BLOCKED for {Target}; the API will not start. {Reason}"
    )]
    private static partial void LogSchemaBlocked(ILogger logger, string target, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Applying pending migrations to {Target}: {Migrations}"
    )]
    private static partial void LogApplying(ILogger logger, string target, string migrations);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Migrations applied to {Target}: {Migrations}"
    )]
    private static partial void LogApplied(ILogger logger, string target, string migrations);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Applying migrations to {Target} FAILED; the API will not start."
    )]
    private static partial void LogMigrationFailed(
        ILogger logger,
        Exception exception,
        string target
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database {Target} matches the model and all migrations are applied; running development seeders."
    )]
    private static partial void LogUpToDate(ILogger logger, string target);
}

/// <summary>The migration policy as a pure decision, so every case is testable without a database.</summary>
public static class DevelopmentMigrationGuard
{
    private const string Tooling = "-p src/OpsFlow.Infrastructure -s src/OpsFlow.Api";

    /// <summary>Auto-apply needs BOTH the Development environment and the explicit flag; any other environment never migrates, whatever the flag says.</summary>
    public static bool ShouldAutoApply(IHostEnvironment environment, bool flag) =>
        flag && environment.IsDevelopment();

    public static void EnsureUpToDate(
        bool hasModelChanges,
        IReadOnlyCollection<string> pendingMigrations,
        bool autoApplyEnabled = false
    )
    {
        var problems = new List<string>();
        if (hasModelChanges)
        {
            problems.Add(
                $"The EF model has changes that no migration covers. Generate and REVIEW a migration (from the backend folder; startup never generates one): dotnet ef migrations add <Name> {Tooling} -o Persistence/Migrations"
            );
        }

        if (pendingMigrations.Count > 0)
        {
            problems.Add(
                $"{pendingMigrations.Count} migration(s) are not applied: {string.Join(", ", pendingMigrations)}. Apply them yourself: dotnet ef database update {Tooling}"
            );
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                string.Join(' ', problems)
                    + (
                        autoApplyEnabled
                            ? " Automatic migration is enabled but did not leave the database up to date."
                            : " Startup does not migrate automatically (set Database:AutoApplyMigrationsOnStartup=true in Development to apply existing migrations)."
                    )
            );
        }
    }
}
