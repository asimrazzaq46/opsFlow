using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpsFlow.Realtime;

// Scoped: the host's IRealtimeGroupMembership usually needs a scoped resource (e.g. a DbContext).
internal sealed partial class RealtimePublisher(IHubContext<RealtimeHub> hub, IServiceProvider services, ILogger<RealtimePublisher> logger) : IRealtimePublisher
{
    private static readonly ConcurrentDictionary<Type, string> Names = new();

    public Task NotifyUserAsync<TEvent>(string userId, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull =>
        SendAsync(() => hub.Clients.User(RequireId(userId, nameof(userId))), payload, cancellationToken);

    public Task NotifyUsersAsync<TEvent>(IReadOnlyCollection<string> userIds, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull =>
        SendToUsersAsync(userIds, payload, cancellationToken);

    public Task SendToUsersAsync(IReadOnlyCollection<string> userIds, object payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(payload);
        foreach (var id in userIds)
        {
            RequireId(id, nameof(userIds));
        }

        if (userIds.Count == 0)
        {
            EventName(payload.GetType()); // still validate the configuration
            return Task.CompletedTask;
        }

        return SendAsync(() => hub.Clients.Users(userIds as IReadOnlyList<string> ?? userIds.ToList()), payload, cancellationToken);
    }

    public Task PublishGroupAsync<TEvent>(string group, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull =>
        SendAsync(() => hub.Clients.Group(RequireId(group, nameof(group))), payload, cancellationToken);

    public async Task PublishToGroupMembersAsync<TEvent>(string group, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        RequireId(group, nameof(group));
        var membership = services.GetService<IRealtimeGroupMembership>()
            ?? throw new InvalidOperationException("Register an IRealtimeGroupMembership to publish to private groups.");
        await SendToUsersAsync(await membership.GetMemberUserIdsAsync(group, cancellationToken), payload, cancellationToken);
    }

    // Invalid configuration throws; a transport failure is best effort (the host's database is the source of truth, so a failed push must
    // never fail an already committed operation); cancellation is honoured and propagated.
    private async Task SendAsync(Func<IClientProxy> target, object payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var name = EventName(payload.GetType());
        var proxy = target();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await proxy.SendAsync("Receive", name, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, name);
        }
    }

    private static string RequireId(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty id is required.", parameter) : value;

    internal static string EventName(Type type) => Names.GetOrAdd(type, t =>
        t.GetCustomAttribute<RealtimeEventAttribute>()?.Name is { Length: > 0 } name ? name : throw new InvalidOperationException($"{t.Name} is missing [RealtimeEvent(\"name\")]."));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime delivery of {EventName} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string eventName);
}
