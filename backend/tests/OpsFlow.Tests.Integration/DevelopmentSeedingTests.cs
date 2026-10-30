using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Infrastructure.Initialization;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>The development seeder, run directly (the Development startup hook itself is never active in test hosts).</summary>
public class DevelopmentSeedingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string StrongPassword = "Seed!Str0ng#Pass1";

    private static DevelopmentSeedOptions NewOptions(string? password = StrongPassword) => new()
    {
        AdminEmail = $"seed-{Guid.NewGuid():N}@example.com",
        OrganizationName = $"Demo {Guid.NewGuid():N}",
        AdminPassword = password,
    };

    private async Task RunAsync(DevelopmentSeedOptions options)
    {
        using var scope = factory.Services.CreateScope();
        var seeder = ActivatorUtilities.CreateInstance<DevelopmentAdminSeeder>(scope.ServiceProvider, Options.Create(options));
        await seeder.SeedAsync(CancellationToken.None);
    }

    private async Task<(int Users, int Tenants, int Memberships)> CountsAsync(DevelopmentSeedOptions options)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return (
            await db.Users.CountAsync(u => u.Email == options.AdminEmail),
            await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == options.OrganizationName),
            await db.Memberships.IgnoreQueryFilters().CountAsync(m => db.Users.Any(u => u.Id == m.UserId && u.Email == options.AdminEmail)));
    }

    [Fact]
    public async Task Seeder_creates_a_confirmed_admin_who_is_the_active_tenant_owner_and_can_sign_in()
    {
        var options = NewOptions();

        await RunAsync(options);

        Assert.Equal((1, 1, 1), await CountsAsync(options));
        var admin = factory.CreateApiClient();
        Assert.True((await admin.LoginAsync(options.AdminEmail, StrongPassword)).IsSuccessStatusCode);
        var orgs = await admin.ReadAsync<List<OrgDto>>(await admin.GetAsync("/api/v1/organizations"));
        Assert.Equal("TenantOwner", Assert.Single(orgs, o => o.Name == options.OrganizationName).Role);
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_anything()
    {
        var options = NewOptions();

        await RunAsync(options);
        await RunAsync(options);

        Assert.Equal((1, 1, 1), await CountsAsync(options));
    }

    [Fact]
    public async Task A_preexisting_unrelated_account_is_never_granted_access_or_re_passworded()
    {
        var stranger = await ApiClient.RegisterAsync(factory);
        var options = NewOptions();
        options.AdminEmail = stranger.Email;

        await RunAsync(options);

        Assert.Equal((1, 0, 0), await CountsAsync(options));
        Assert.True((await factory.CreateApiClient().LoginAsync(stranger.Email, ApiClient.Password)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().LoginAsync(stranger.Email, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_password_nothing_is_created()
    {
        var options = NewOptions(password: null);

        await RunAsync(options);

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }

    [Fact]
    public async Task A_weak_password_fails_clearly_and_leaves_nothing_behind()
    {
        var options = NewOptions("weak");

        await Assert.ThrowsAsync<OpsFlow.Application.Common.AppException>(() => RunAsync(options)); // password policy rejects it

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }

    [PostgresOnlyFact]
    public async Task A_failure_after_the_user_was_created_rolls_the_whole_seed_back()
    {
        var options = NewOptions();
        options.OrganizationName = new string('x', 101); // exceeds Tenants.Name varchar(100) only on PostgreSQL

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => RunAsync(options));

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }
}

/// <summary>Development startup policy: model drift and failed/unapplied migrations stop the host; existing migrations are applied only in Development with the explicit flag.</summary>
public class DevelopmentStartupTests
{
    [Fact]
    public void Model_changes_without_a_migration_block_startup_and_explain_how_to_generate_one()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DevelopmentMigrationGuard.EnsureUpToDate(true, []));

        Assert.Contains("no migration covers", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet ef migrations add", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not applied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unapplied_migrations_block_startup_and_are_named()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DevelopmentMigrationGuard.EnsureUpToDate(false, ["20260101_A", "20260102_B"]));

        Assert.Contains("2 migration(s) are not applied: 20260101_A, 20260102_B", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet ef database update", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no migration covers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_problems_are_reported_together_and_an_up_to_date_database_passes()
    {
        var both = Assert.Throws<InvalidOperationException>(() => DevelopmentMigrationGuard.EnsureUpToDate(true, ["X"]));
        Assert.Contains("no migration covers", both.Message, StringComparison.Ordinal);
        Assert.Contains("not applied: X", both.Message, StringComparison.Ordinal);

        DevelopmentMigrationGuard.EnsureUpToDate(false, []); // no exception: seeding may proceed
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Staging", true, false)] // the flag alone never migrates outside Development
    [InlineData("Production", true, false)]
    [InlineData("Testing", true, false)]
    public void Auto_apply_needs_both_the_development_environment_and_the_explicit_flag(string environment, bool flag, bool expected) =>
        Assert.Equal(expected, DevelopmentMigrationGuard.ShouldAutoApply(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment }, flag));

    private sealed class FakeSchema(IEnumerable<string> pending, bool drift, Exception? migrationFailure = null, bool leavesPending = false) : ISchemaState
    {
        private List<string> _pending = [.. pending];

        public int Migrations { get; private set; }

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(_pending.ToList());

        public bool HasModelChanges() => drift;

        public Task MigrateAsync(CancellationToken cancellationToken)
        {
            Migrations++;
            if (migrationFailure is not null)
            {
                throw migrationFailure;
            }

            if (!leavesPending)
            {
                _pending = [];
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private const string Target = "localhost:5434/opsflow";

    [Fact]
    public async Task Enabled_with_pending_migrations_applies_them_verifies_and_logs_only_names()
    {
        var schema = new FakeSchema(["20260101_A", "20260102_B"], drift: false);
        var logger = new ListLogger();

        await DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: true, logger, Target, CancellationToken.None);

        Assert.Equal(1, schema.Migrations);
        Assert.Contains(logger.Entries, e => e.Text.Contains("Applying pending migrations", StringComparison.Ordinal) && e.Text.Contains("20260101_A, 20260102_B", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Text.Contains("Migrations applied", StringComparison.Ordinal));
        Assert.All(logger.Entries, e => Assert.DoesNotContain("Password", e.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Enabled_without_pending_migrations_starts_normally_and_migrates_nothing()
    {
        var schema = new FakeSchema([], drift: false);

        await DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: true, new ListLogger(), Target, CancellationToken.None);

        Assert.Equal(0, schema.Migrations);
    }

    [Fact]
    public async Task Model_drift_fails_clearly_and_never_migrates_or_generates_anything()
    {
        var schema = new FakeSchema(["20260101_A"], drift: true);
        var logger = new ListLogger();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: true, logger, Target, CancellationToken.None));

        Assert.Equal(0, schema.Migrations); // drift is rejected BEFORE anything is applied
        Assert.Contains("no migration covers", ex.Message, StringComparison.Ordinal);
        Assert.Contains("startup never generates one", ex.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task A_failing_migration_is_logged_and_blocks_startup()
    {
        var failure = new InvalidOperationException("boom");
        var schema = new FakeSchema(["20260101_A"], drift: false, migrationFailure: failure);
        var logger = new ListLogger();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: true, logger, Target, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Text.Contains("FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrations_that_remain_pending_after_applying_block_startup()
    {
        var schema = new FakeSchema(["20260101_A"], drift: false, leavesPending: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: true, new ListLogger(), Target, CancellationToken.None));

        Assert.Contains("did not leave the database up to date", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_keeps_the_fail_fast_behaviour_and_names_the_switch()
    {
        var schema = new FakeSchema(["20260101_A"], drift: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply: false, new ListLogger(), Target, CancellationToken.None));

        Assert.Equal(0, schema.Migrations);
        Assert.Contains("1 migration(s) are not applied: 20260101_A", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Database:AutoApplyMigrationsOnStartup", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task Outside_development_the_flag_is_ignored_so_nothing_is_migrated(string environment)
    {
        var autoApply = DevelopmentMigrationGuard.ShouldAutoApply(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment }, flag: true);
        var schema = new FakeSchema(["20260101_A"], drift: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentSchemaStep.EnsureReadyAsync(schema, autoApply, new ListLogger(), Target, CancellationToken.None));

        Assert.Equal(0, schema.Migrations);
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)] // production never runs the development hook (and nothing migrates anywhere)
    [InlineData("Testing", true, false)]
    public void The_startup_check_is_registered_only_in_enabled_development(string environment, bool enabled, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentInitialization:Enabled"] = enabled.ToString() }).Build();
        var services = new ServiceCollection();

        services.AddDevelopmentInitialization(configuration, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment });

        Assert.Equal(expected, services.Any(s => s.ImplementationType == typeof(DevelopmentInitializer)));
    }

    [PostgresOnlyFact]
    public async Task The_host_refuses_to_start_on_an_unmigrated_database_and_changes_nothing()
    {
        var name = $"opsflow_startup_{Guid.NewGuid():N}";
        var connectionString = TestDatabase.CreatePostgresDatabase(name);
        try
        {
            await using var blocked = new DevelopmentHostFactory(connectionString);

            var failure = Assert.ThrowsAny<Exception>(() => blocked.CreateClient()); // starting the host runs the hook BEFORE the server listens

            var message = Flatten(failure);
            Assert.Contains("migration(s) are not applied", message, StringComparison.Ordinal);
            Assert.Contains("InitialCreate", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")); // nothing migrated, created or seeded
        }
        finally
        {
            TestDatabase.DropPostgresDatabase(name);
        }
    }

    [PostgresOnlyFact]
    public async Task Staging_with_the_flag_enabled_never_migrates_the_database()
    {
        var name = $"opsflow_startup_{Guid.NewGuid():N}";
        var connectionString = TestDatabase.CreatePostgresDatabase(name);
        try
        {
            await using var staging = new DevelopmentHostFactory(connectionString, environment: "Staging", autoApply: true);

            var failure = Assert.ThrowsAny<Exception>(() => staging.CreateClient());

            Assert.Contains("migration(s) are not applied", Flatten(failure), StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        }
        finally
        {
            TestDatabase.DropPostgresDatabase(name);
        }
    }

    [PostgresOnlyFact]
    public async Task A_development_host_with_the_flag_applies_existing_migrations_before_serving_or_fails_on_model_drift()
    {
        var name = $"opsflow_startup_{Guid.NewGuid():N}";
        var connectionString = TestDatabase.CreatePostgresDatabase(name);
        try
        {
            bool drift;
            await using (var probe = new OpsFlowDbContext(new DbContextOptionsBuilder<OpsFlowDbContext>().UseNpgsql(connectionString).Options, new OpsFlow.Application.Abstractions.TenantContext()))
            {
                drift = probe.Database.HasPendingModelChanges(); // the repository's own state: are all model changes covered by a migration?
            }

            await using var development = new DevelopmentHostFactory(connectionString, environment: "Development", autoApply: true);
            if (drift)
            {
                var failure = Assert.ThrowsAny<Exception>(() => development.CreateClient());
                Assert.Contains("no migration covers", Flatten(failure), StringComparison.Ordinal);
                Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")); // nothing applied while drift exists
                return;
            }

            Assert.Equal(HttpStatusCode.OK, (await development.CreateClient().GetAsync("/api/v1/health")).StatusCode); // served only after migrating + seeding
            Assert.True(await ScalarAsync(connectionString, "SELECT count(*) FROM \"__EFMigrationsHistory\"") > 0);
            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM \"Tenants\" WHERE \"Name\" = 'Startup Demo'"));
        }
        finally
        {
            TestDatabase.DropPostgresDatabase(name);
        }
    }

    [PostgresOnlyFact]
    public async Task A_development_host_with_the_flag_disabled_keeps_the_fail_fast_behaviour()
    {
        var name = $"opsflow_startup_{Guid.NewGuid():N}";
        var connectionString = TestDatabase.CreatePostgresDatabase(name);
        try
        {
            await using var development = new DevelopmentHostFactory(connectionString, environment: "Development", autoApply: false);

            var failure = Assert.ThrowsAny<Exception>(() => development.CreateClient());

            Assert.Contains("migration(s) are not applied", Flatten(failure), StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        }
        finally
        {
            TestDatabase.DropPostgresDatabase(name);
        }
    }

    [PostgresOnlyFact]
    public async Task An_up_to_date_database_starts_seeds_once_and_is_never_migrated_by_the_host()
    {
        var name = $"opsflow_startup_{Guid.NewGuid():N}";
        var connectionString = TestDatabase.CreatePostgresDatabase(name);
        try
        {
            await using (var db = new OpsFlowDbContext(new DbContextOptionsBuilder<OpsFlowDbContext>().UseNpgsql(connectionString).Options, new OpsFlow.Application.Abstractions.TenantContext()))
            {
                await db.Database.MigrateAsync(); // the developer's manual 'dotnet ef database update', on an isolated test database
            }

            var applied = await ScalarAsync(connectionString, "SELECT count(*) FROM \"__EFMigrationsHistory\"");
            await using (var first = new DevelopmentHostFactory(connectionString))
            {
                Assert.Equal(HttpStatusCode.OK, (await first.CreateClient().GetAsync("/api/v1/health")).StatusCode); // serves only after the hook succeeded
            }

            await using (var second = new DevelopmentHostFactory(connectionString))
            {
                second.CreateClient(); // restart: idempotent
            }

            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM \"Users\" WHERE \"Email\" = 'startup-admin@example.com'"));
            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM \"Tenants\" WHERE \"Name\" = 'Startup Demo'"));
            Assert.True(await ScalarAsync(connectionString, "SELECT count(*) FROM \"TicketCategories\"") > 0); // ticket defaults were ensured
            Assert.Equal(applied, await ScalarAsync(connectionString, "SELECT count(*) FROM \"__EFMigrationsHistory\"")); // the host applied no migration
        }
        finally
        {
            TestDatabase.DropPostgresDatabase(name);
        }
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string Flatten(Exception ex) => string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
        }
    }

    /// <summary>A Development-style host (verify-only hook + seeders registered) over an isolated database.</summary>
    private sealed class DevelopmentHostFactory(string connectionString, string environment = "Testing", bool autoApply = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Jwt:SigningKey"] = "integration-test-signing-key-0123456789-abcdefghij",
                ["Auth:Password:Iterations"] = "1000",
                ["Database:AutoApplyMigrationsOnStartup"] = autoApply.ToString(),
                ["DevelopmentInitialization:Enabled"] = "true",
                ["DevelopmentSeed:AdminEmail"] = "startup-admin@example.com",
                ["DevelopmentSeed:OrganizationName"] = "Startup Demo",
                ["DevelopmentSeed:AdminPassword"] = "Seed!Str0ng#Pass1",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<OpsFlowDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<OpsFlowDbContext>>();
                services.AddDbContext<OpsFlowDbContext>(o => o.UseNpgsql(connectionString));
                if (environment != "Development") // a real Development host registers the hook and seeders itself (Program.cs)
                {
                    services.AddScoped<IDevelopmentSeeder, DevelopmentAdminSeeder>();
                    services.AddScoped<IDevelopmentSeeder, DevelopmentTenantDefaultsSeeder>();
                    services.AddHostedService<DevelopmentInitializer>();
                }
            });
        }
    }
}
