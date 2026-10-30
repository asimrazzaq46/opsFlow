namespace OpsFlow.Application.Abstractions;

/// <summary>Resource names of tenant-scoped cache entries (one place, so readers and invalidators cannot drift apart).</summary>
public static class CacheResources
{
    public const string Categories = "categories";
    public const string SlaPolicies = "sla-policies";
}

/// <summary>
/// Cache-aside for NON-sensitive, tenant-wide data only (never user-specific or private content). PostgreSQL stays the source of truth:
/// every failure of the cache falls back to the factory. Callers authorize BEFORE reading.
/// </summary>
public interface ITenantCache
{
    Task<T> GetOrCreateAsync<T>(
        Guid tenantId,
        string resource,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken
    );

    /// <summary>Makes every entry of the resource unreachable. False when the cache backend could not be updated (the outbox retries it).</summary>
    Task<bool> InvalidateAsync(Guid tenantId, string resource, CancellationToken cancellationToken);
}

/// <summary>Used when no Redis is configured: always reads the database.</summary>
public sealed class NoCache : ITenantCache
{
    public Task<T> GetOrCreateAsync<T>(
        Guid tenantId,
        string resource,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken
    ) => factory(cancellationToken);

    public Task<bool> InvalidateAsync(
        Guid tenantId,
        string resource,
        CancellationToken cancellationToken
    ) => Task.FromResult(true);
}
