using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace OpsFlow.Realtime;

/// <summary>Infrastructure (public only because the hub constructor needs it). Per-instance bookkeeping of which authenticated user's connection joined which groups (needed to evict on revocation).</summary>
public sealed class RealtimeConnectionRegistry
{
    private sealed record Entry(string UserId, HashSet<string> Groups);

    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    internal void Add(string connectionId, string? userId)
    {
        if (userId is not null)
        {
            _connections[connectionId] = new Entry(userId, []);
        }
    }

    internal void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

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

    internal IReadOnlyList<(string ConnectionId, string Group)> Memberships(string userId, string? group)
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
                result.AddRange(entry.Groups.Where(g => group is null || g == group).Select(g => (connectionId, g)));
            }
        }

        return result;
    }
}

internal sealed class RealtimeGroupManager(RealtimeConnectionRegistry registry, IHubContext<RealtimeHub> hub) : IRealtimeGroupManager
{
    public async Task RevokeAsync(string userId, string group, CancellationToken cancellationToken = default)
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

    private async Task RemoveAsync(IReadOnlyList<(string ConnectionId, string Group)> memberships, CancellationToken cancellationToken)
    {
        foreach (var (connectionId, group) in memberships)
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken);
            registry.Left(connectionId, group);
        }
    }
}
