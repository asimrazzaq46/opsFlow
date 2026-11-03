using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace OpsFlow.Realtime;

/// <summary>
/// Infrastructure (public only because the hub constructor needs it). Per-instance bookkeeping of every live connection: its authenticated
/// principal (for revalidation), when it was last verified against the host, and which groups it joined (needed to evict on revocation).
/// </summary>
public sealed class RealtimeConnectionRegistry
{
    private sealed class Entry(
        string? userId,
        ClaimsPrincipal user,
        Action abort,
        long verifiedTicks,
        CancellationToken ended
    )
    {
        private long _verifiedTicks = verifiedTicks;

        public string? UserId { get; } = userId;

        public ClaimsPrincipal User { get; } = user;

        public Action Abort { get; } = abort;

        public CancellationToken Ended { get; } = ended;

        public HashSet<string> Groups { get; } = [];

        public DateTimeOffset VerifiedAt =>
            new(new DateTime(Interlocked.Read(ref _verifiedTicks), DateTimeKind.Utc));

        public void MarkVerified(DateTimeOffset at) =>
            Interlocked.Exchange(ref _verifiedTicks, at.UtcTicks);
    }

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    /// <summary>A connection as seen by the revalidation monitor; <see cref="Abort"/> is safe to call from any thread and any number of times.</summary>
    internal sealed record Tracked(
        string ConnectionId,
        ClaimsPrincipal User,
        DateTimeOffset VerifiedAt,
        Action Abort
    );

    /// <summary>Every connection is tracked, even without a user identifier: revalidation must see (and reject) it too.</summary>
    internal void Add(
        string connectionId,
        string? userId,
        ClaimsPrincipal user,
        Action abort,
        DateTimeOffset verifiedAt,
        CancellationToken ended
    ) =>
        _connections[connectionId] = new Entry(userId, user, abort, verifiedAt.UtcTicks, ended);

    internal void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    internal int Count => _connections.Count;

    internal void Joined(string connectionId, string group)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            lock (entry.Groups)
            {
                entry.Groups.Add(group);
            }
        }
    }

    internal void Left(string connectionId, string group)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            lock (entry.Groups)
            {
                entry.Groups.Remove(group);
            }
        }
    }

    internal IReadOnlyList<(string ConnectionId, string Group)> Memberships(
        string userId,
        string? group
    )
    {
        List<(string, string)> result = [];
        foreach (var (connectionId, entry) in _connections)
        {
            if (entry.UserId != userId)
            {
                continue;
            }

            lock (entry.Groups)
            {
                result.AddRange(
                    entry
                        .Groups.Where(g => group is null || g == group)
                        .Select(g => (connectionId, g))
                );
            }
        }

        return result;
    }

    /// <summary>Connections that are still open. Entries whose connection already ended are dropped here as a safety net against a missed disconnect callback.</summary>
    internal IReadOnlyList<Tracked> Snapshot()
    {
        List<Tracked> result = [];
        foreach (var (connectionId, entry) in _connections)
        {
            if (entry.Ended.IsCancellationRequested)
            {
                _connections.TryRemove(connectionId, out _);
                continue;
            }

            result.Add(new Tracked(connectionId, entry.User, entry.VerifiedAt, entry.Abort));
        }

        return result;
    }

    internal void MarkVerified(string connectionId, DateTimeOffset at)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
        {
            entry.MarkVerified(at);
        }
    }
}

internal sealed class RealtimeGroupManager(
    RealtimeConnectionRegistry registry,
    IHubContext<RealtimeHub> hub
) : IRealtimeGroupManager
{
    public async Task RevokeAsync(
        string userId,
        string group,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        await RemoveAsync(registry.Memberships(userId, group), cancellationToken);
    }

    public async Task RevokeUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await RemoveAsync(registry.Memberships(userId, null), cancellationToken);
    }

    private async Task RemoveAsync(
        IReadOnlyList<(string ConnectionId, string Group)> memberships,
        CancellationToken cancellationToken
    )
    {
        foreach (var (connectionId, group) in memberships)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken);
            registry.Left(connectionId, group);
        }
    }
}
