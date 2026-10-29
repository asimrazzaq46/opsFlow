using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    protected virtual int SensitivePermitLimit => 100_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:GlobalPermitLimit"] = "100000",
            ["RateLimiting:SensitivePermitLimit"] = SensitivePermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<OpsFlowDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<OpsFlowDbContext>>();
            services.AddDbContext<OpsFlowDbContext>(o =>
            {
                if (_postgresConnectionString is not null)
                {
                    o.UseNpgsql(_postgresConnectionString);
                }
                else
                {
                    o.UseSqlite($"Data Source={DbPath}");
                }
            });
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

    public ApiClient CreateApiClient() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
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
