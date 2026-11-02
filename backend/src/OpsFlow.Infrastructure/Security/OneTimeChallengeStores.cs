using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;
using OpsFlow.Infrastructure.Caching;
using StackExchange.Redis;

namespace OpsFlow.Infrastructure.Security;

/// <summary>
/// Redis-backed challenges shared by every API instance; GETDEL makes consumption atomic. Unlike the tenant cache this store fails
/// CLOSED: while Redis is unreachable no challenge can be issued or consumed (503), so sign-in never proceeds without replay protection.
/// </summary>
internal sealed partial class RedisChallengeStore(
    IOptions<CacheOptions> options,
    ILogger<RedisChallengeStore> logger
) : IOneTimeChallengeStore, IDisposable
{
    private const string Prefix = "opsflow:auth:challenge:v1:";

    private readonly Lazy<ConnectionMultiplexer> _connection = new(
        () =>
            ConnectionMultiplexer.Connect(
                RedisTenantCache.BuildConfiguration(options.Value.RedisConnectionString!)
            ),
        LazyThreadSafetyMode.PublicationOnly
    );

    public Task AddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken) =>
        RunAsync(db => db.StringSetAsync(Prefix + key, value, lifetime));

    public Task<string?> TakeAsync(string key, CancellationToken cancellationToken) =>
        RunAsync(async db => (string?)await db.StringGetDeleteAsync(Prefix + key));

    private async Task<T> RunAsync<T>(Func<IDatabase, Task<T>> action)
    {
        try
        {
            return await action(_connection.Value.GetDatabase());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogUnavailable(logger, ex.GetType().Name);
            throw ChallengeStore.Unavailable();
        }
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Authentication challenge store unavailable ({ErrorType})")]
    private static partial void LogUnavailable(ILogger logger, string errorType);
}

/// <summary>
/// In-process challenges, used only when no Redis is configured (single-instance development and tests).
/// shortcut: not shared between instances; a multi-instance deployment must set Cache:RedisConnectionString. A challenge issued by
/// another instance is simply unknown here, so the failure mode is a rejected sign-in, never a bypass.
/// </summary>
internal sealed class InMemoryChallengeStore(TimeProvider clock) : IOneTimeChallengeStore
{
    private const int MaxEntries = 100_000;
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _entries = new();

    public Task AddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_entries.Count >= MaxEntries)
        {
            foreach (var expired in _entries.Where(e => e.Value.ExpiresAt <= now))
            {
                _entries.TryRemove(expired.Key, out _);
            }

            if (_entries.Count >= MaxEntries)
            {
                throw ChallengeStore.Unavailable(); // bounded memory under a challenge flood
            }
        }

        _entries[key] = (value, now + lifetime);
        return Task.CompletedTask;
    }

    // TryRemove is atomic: of two concurrent consumers exactly one receives the value.
    public Task<string?> TakeAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(
            _entries.TryRemove(key, out var entry) && entry.ExpiresAt > clock.GetUtcNow()
                ? entry.Value
                : null
        );
}

internal static class ChallengeStore
{
    public static AppException Unavailable() =>
        new AppException(503, "Sign-in is temporarily unavailable.", "Please try again shortly.").WithCode(
            "AUTH_GOOGLE_UNAVAILABLE"
        );
}
