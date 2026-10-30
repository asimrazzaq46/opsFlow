using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Tests.Integration.Support;

/// <summary>Same generation-based semantics as the Redis cache, in memory: lets tests observe hits/misses and invalidation without Redis.</summary>
public sealed class InMemoryTenantCache : ITenantCache
{
    private readonly ConcurrentDictionary<string, object> _entries = new();
    private readonly ConcurrentDictionary<string, int> _generations = new();

    public int Loads { get; private set; }

    public async Task<T> GetOrCreateAsync<T>(Guid tenantId, string resource, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        var key = $"{tenantId:N}:{resource}:g{_generations.GetValueOrDefault($"{tenantId:N}:{resource}")}";
        if (_entries.TryGetValue(key, out var hit))
        {
            return (T)hit;
        }

        var fresh = await factory(cancellationToken);
        Loads++;
        _entries[key] = fresh!;
        return fresh;
    }

    public Task<bool> InvalidateAsync(Guid tenantId, string resource, CancellationToken cancellationToken)
    {
        _generations.AddOrUpdate($"{tenantId:N}:{resource}", 1, (_, g) => g + 1);
        return Task.FromResult(true);
    }
}

public sealed class CachingApiFactory : ApiFactory
{
    public InMemoryTenantCache Cache { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantCache>();
            services.AddSingleton<ITenantCache>(Cache);
        });
    }
}

public static class BackgroundTestExtensions
{
    public static async Task<SlaScanResult> ScanSlaAsync(this ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SlaMonitor>().ScanAsync(CancellationToken.None);
    }

    public static async Task<T> WithDbAsync<T>(this ApiFactory factory, Func<OpsFlowDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>());
    }

    /// <summary>Provider-neutral (SQLite stores GUIDs upper-case, so raw SQL literals would not match).</summary>
    public static Task<int> SuspendMembershipAsync(this ApiFactory factory, Guid membershipId) =>
        factory.WithDbAsync(db => db.Memberships.IgnoreQueryFilters().Where(m => m.Id == membershipId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MembershipStatus.Inactive)));

    public static Task<List<OutboxMessage>> OutboxAsync(this ApiFactory factory, Guid tenantId, string? eventType = null) =>
        factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().Where(m => m.TenantId == tenantId && (eventType == null || m.EventType == eventType)).OrderBy(m => m.CreatedAtUtc).ToListAsync());

    public static Task<List<TicketSlaCycle>> CyclesAsync(this ApiFactory factory, Guid ticketId) =>
        factory.WithDbAsync(db => db.TicketSlaCycles.IgnoreQueryFilters().AsNoTracking().Where(c => c.TicketId == ticketId).OrderBy(c => c.CycleNumber).ToListAsync());

    public static Task<int> SlaNotificationsAsync(this ApiFactory factory, Guid userId, NotificationType type, string detail) =>
        factory.WithDbAsync(db => db.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == userId && n.Type == type && n.Detail == detail));
}

public sealed record CapturedLog(LogLevel Level, string Message);

/// <summary>Host whose log output can be inspected (warnings/diagnostics are part of the behaviour under test).</summary>
public sealed class LoggingApiFactory : ApiFactory
{
    private readonly CapturingProvider _provider = new();

    public IReadOnlyCollection<CapturedLog> Logs => _provider.Entries.ToArray();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(_provider));
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception)));
    }
}
