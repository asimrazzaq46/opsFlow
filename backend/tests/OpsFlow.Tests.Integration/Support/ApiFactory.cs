using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Tests.Integration.Support;

public sealed record SentEmail(string To, string Subject, string Body);

public sealed class FakeEmailService : IEmailService
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        _sent.Enqueue(new SentEmail(recipient, subject, body));
        return Task.CompletedTask;
    }

    public SentEmail Last(string to, string bodyContains) =>
        _sent.Last(e => string.Equals(e.To, to, StringComparison.OrdinalIgnoreCase) && e.Body.Contains(bodyContains, StringComparison.Ordinal));

    public int Count(string to) => _sent.Count(e => string.Equals(e.To, to, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Selects the test database: OPSFLOW_TEST_DB=postgres runs against a real PostgreSQL Testcontainer
/// (one container per test run, one fresh database per factory); anything else uses a throw-away SQLite file.
/// Schema comes from EnsureCreated() (current EF model) - this is NOT migration verification.
/// </summary>
public static class TestDatabase
{
    public static readonly bool UsePostgres = string.Equals(Environment.GetEnvironmentVariable("OPSFLOW_TEST_DB"), "postgres", StringComparison.OrdinalIgnoreCase);

    public static string Provider => UsePostgres ? "PostgreSQL" : "SQLite";

    private static readonly Lazy<PostgreSqlContainer> Container = new(() =>
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return container;
    });

    public static string CreatePostgresDatabase(string name)
    {
        using var admin = new NpgsqlConnection(Container.Value.GetConnectionString());
        admin.Open();
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
        create.ExecuteNonQuery();
        return new NpgsqlConnectionStringBuilder(Container.Value.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public static void DropPostgresDatabase(string name)
    {
        NpgsqlConnection.ClearAllPools();
        using var admin = new NpgsqlConnection(Container.Value.GetConnectionString());
        admin.Open();
        using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        drop.ExecuteNonQuery();
    }
}

/// <summary>
/// Test-only concurrency gate (PostgreSQL tests): pauses ONE chosen DbContext right after its next COUNT query so a test can
/// let a competing transaction commit at that exact point (deterministic interleaving, bounded waits).
/// </summary>
public sealed class QueryGate : DbCommandInterceptor, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _paused = new(0);
    private readonly SemaphoreSlim _resume = new(0);
    private volatile DbContext? _target;
    private volatile string _fragment = "COUNT(";

    public void PauseAfterNextCount(DbContext target) => PauseAfterNextQuery(target, "COUNT(");

    public void PauseAfterNextQuery(DbContext target, string sqlFragment)
    {
        _fragment = sqlFragment;
        _target = target;
    }

    public Task<bool> WaitUntilPausedAsync() => _paused.WaitAsync(Timeout);

    public void Resume() => _resume.Release();

    public void Dispose()
    {
        _paused.Dispose();
        _resume.Dispose();
    }

    // EF runs COUNT through a reader; the count value is already fixed at execution time, so pausing here
    // freezes the request with a "stale" owner count. (PostgreSQL only: an open reader would block SQLite writers.)
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(eventData.Context, _target) && command.CommandText.Contains(_fragment, StringComparison.OrdinalIgnoreCase))
        {
            _target = null;
            _paused.Release();
            if (!await _resume.WaitAsync(Timeout, cancellationToken))
            {
                throw new TimeoutException("QueryGate was never resumed.");
            }
        }

        return result;
    }
}

/// <summary>Test-only: freezes ONE chosen DbContext right before its next SaveChanges (after all its reads), so a competing request can commit first.</summary>
public sealed class SaveGate : SaveChangesInterceptor, IDisposable
{
    private readonly SemaphoreSlim _paused = new(0);
    private readonly SemaphoreSlim _resume = new(0);
    private volatile DbContext? _target;

    public void PauseNextSave(DbContext target) => _target = target;

    public Task<bool> WaitUntilPausedAsync() => _paused.WaitAsync(QueryGate.Timeout);

    public void Resume() => _resume.Release();

    public void Dispose()
    {
        _paused.Dispose();
        _resume.Dispose();
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(eventData.Context, _target))
        {
            _target = null;
            _paused.Release();
            if (!await _resume.WaitAsync(QueryGate.Timeout, cancellationToken))
            {
                throw new TimeoutException("SaveGate was never resumed.");
            }
        }

        return result;
    }
}

/// <summary>Test-only fault injection: makes the next SaveChanges fail with a non-unique persistence error.</summary>
public sealed class SaveFaultInjector : SaveChangesInterceptor
{
    private int _armed;

    public void FailNextSave() => Interlocked.Exchange(ref _armed, 1);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _armed, 0) == 1
            ? throw new DbUpdateException("Injected persistence failure", new InvalidOperationException("injected-secret-detail"))
            : base.SavingChangesAsync(eventData, result, cancellationToken);
}

/// <summary>Controllable clock: auth lifetimes (access/refresh expiry, lockout, one-time tokens) are tested by advancing it.</summary>
public sealed class TestClock : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>Xunit fact that only runs when the suite targets PostgreSQL.</summary>
public sealed class PostgresOnlyFactAttribute : FactAttribute
{
    public PostgresOnlyFactAttribute()
    {
        if (!TestDatabase.UsePostgres)
        {
            Skip = "PostgreSQL-only test (set OPSFLOW_TEST_DB=postgres).";
        }
    }
}

/// <summary>Boots the real API on a throw-away database (own connection per DbContext, so concurrency tests are real).</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"opsflow_test_{Guid.NewGuid():N}";
    private string DbPath => Path.Combine(Path.GetTempPath(), _dbName + ".db");
    private readonly string? _postgresConnectionString = TestDatabase.UsePostgres ? TestDatabase.CreatePostgresDatabase($"opsflow_test_{Guid.NewGuid():N}") : null;

    public FakeEmailService Emails { get; } = new();
    public QueryGate Gate { get; } = new();
    public TestClock Clock { get; } = new();
    public SaveGate SaveGate { get; } = new();
    public SaveFaultInjector Faults { get; } = new();

    protected virtual int SensitivePermitLimit => 100_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:GlobalPermitLimit"] = "100000",
            ["RateLimiting:SensitivePermitLimit"] = SensitivePermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Auth:Jwt:SigningKey"] = "integration-test-signing-key-0123456789-abcdefghij",
            ["Auth:Password:Iterations"] = "1000", // fast hashing for tests only; production enforces >= 210000
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OpsFlowDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OpsFlowDbContext>>();
            services.AddDbContext<OpsFlowDbContext>(o =>
            {
                o.AddInterceptors(Gate, Faults, SaveGate);
                if (_postgresConnectionString is not null)
                {
                    o.UseNpgsql(_postgresConnectionString);
                }
                else
                {
                    o.UseSqlite($"Data Source={DbPath}");
                }
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(Emails);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Database.EnsureCreated();
        return host;
    }

    public ApiClient CreateApiClient() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Gate.Dispose();
            SaveGate.Dispose();
            if (_postgresConnectionString is not null)
            {
                TestDatabase.DropPostgresDatabase(new NpgsqlConnectionStringBuilder(_postgresConnectionString).Database!);
            }
            else
            {
                SqliteConnection.ClearAllPools();
                File.Delete(DbPath);
            }
        }
    }
}

public sealed class RateLimitedApiFactory : ApiFactory
{
    protected override int SensitivePermitLimit => 2;
}
