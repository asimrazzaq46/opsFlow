using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using StackExchange.Redis;

namespace OpsFlow.Infrastructure.Caching;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Empty = caching disabled (every read goes to PostgreSQL). May point to the same Redis as the SignalR backplane.</summary>
    public string? RedisConnectionString { get; set; }

    public int CategoriesTtlSeconds { get; set; } = 600;
    public int SlaPoliciesTtlSeconds { get; set; } = 300;
    public int DefaultTtlSeconds { get; set; } = 300;
}

/// <summary>
/// Redis cache-aside, keyed <c>opsflow:cache:v1:{tenantId}:{resource}:g{generation}</c>. Invalidation increments the resource's generation, so
/// a reader that loaded old data before the change can only write to a generation nobody reads any more (no stale write-back race).
/// Every Redis failure degrades to the database; it is logged at most once per interval, never thrown to the business operation.
/// </summary>
public sealed partial class RedisTenantCache(
    IOptions<CacheOptions> options,
    ILogger<RedisTenantCache> logger,
    TimeProvider clock
) : ITenantCache, IDisposable
{
    private const string Prefix = "opsflow:cache:v1";
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

    // UPDATED: PublicationOnly: fail hua Connect cache nahi hota, agla access dobara try karta hai
    private readonly Lazy<ConnectionMultiplexer> _connection = new(
        () =>
            ConnectionMultiplexer.Connect(BuildConfiguration(options.Value.RedisConnectionString!)),
        LazyThreadSafetyMode.PublicationOnly
    );

    /// <summary>Full StackExchange.Redis connection string (host:port,password=...,ssl=true); throws on a malformed value (validated at startup, see AddInfrastructure).</summary>
    public static ConfigurationOptions BuildConfiguration(string connectionString)
    {
        var configuration = ConfigurationOptions.Parse(connectionString);
        configuration.AbortOnConnectFail = false; // never blocks/crashes startup; reads fall back until Redis is reachable
        configuration.ConnectTimeout = 1000;
        configuration.SyncTimeout = 500;
        configuration.AsyncTimeout = 500;
        return configuration;
    }

    private long _lastLogTicks;

    public async Task<T> GetOrCreateAsync<T>(
        Guid tenantId,
        string resource,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken
    )
    {
        string? key = null;
        try
        {
            var redis = _connection.Value.GetDatabase();
            key =
                $"{Prefix}:{tenantId:N}:{resource}:g{await redis.StringGetAsync(GenerationKey(tenantId, resource))}";
            var cached = await redis.StringGetAsync(key);
            if (cached.HasValue && JsonSerializer.Deserialize<T>((string)cached!) is { } value)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            key = ex is JsonException ? key : null; // a corrupt entry is simply overwritten; a Redis outage skips the write-back too
            LogUnavailable(ex);
        }

        var fresh = await factory(cancellationToken);
        if (key is not null)
        {
            try
            {
                await _connection
                    .Value.GetDatabase()
                    .StringSetAsync(
                        key,
                        JsonSerializer.Serialize(fresh),
                        TimeSpan.FromSeconds(TtlSeconds(resource))
                    );
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogUnavailable(ex);
            }
        }

        return fresh;
    }

    public async Task<bool> InvalidateAsync(
        Guid tenantId,
        string resource,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _connection
                .Value.GetDatabase()
                .StringIncrementAsync(GenerationKey(tenantId, resource));
            return true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }

    private static string GenerationKey(Guid tenantId, string resource) =>
        $"{Prefix}:{tenantId:N}:{resource}:gen";

    private int TtlSeconds(string resource) =>
        resource switch
        {
            CacheResources.Categories => options.Value.CategoriesTtlSeconds,
            CacheResources.SlaPolicies => options.Value.SlaPoliciesTtlSeconds,
            _ => options.Value.DefaultTtlSeconds,
        };

    private void LogUnavailable(Exception ex)
    {
        var now = clock.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastLogTicks);
        if (
            now - last >= LogInterval.Ticks
            && Interlocked.CompareExchange(ref _lastLogTicks, now, last) == last
        )
        {
            LogRedisFallback(ex.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis cache unavailable ({ErrorType}); serving from PostgreSQL (further occurrences suppressed for 30s)"
    )]
    private partial void LogRedisFallback(string errorType);
}
