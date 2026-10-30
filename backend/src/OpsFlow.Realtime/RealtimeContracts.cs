using System.Security.Claims;

namespace OpsFlow.Realtime;

/// <summary>Names the wire event of a payload type, e.g. <c>[RealtimeEvent("ticket.message.created")]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RealtimeEventAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>The single client callback every event is delivered through: <c>connection.on("Receive", (name, payload) => ...)</c>.</summary>
public interface IRealtimeClient
{
    Task Receive(string name, object payload);
}

/// <summary>
/// Server-side publishing. Recipients are decided by the caller from CURRENT permissions at publish time.
/// Invalid input (missing <see cref="RealtimeEventAttribute"/>, empty ids, null payload, missing membership provider) throws
/// <see cref="InvalidOperationException"/>/<see cref="ArgumentException"/>; a transport failure is logged and swallowed (best effort,
/// the host's database stays the source of truth); a cancelled token throws <see cref="OperationCanceledException"/>.
/// </summary>
public interface IRealtimePublisher
{
    Task NotifyUserAsync<TEvent>(string userId, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    Task NotifyUsersAsync<TEvent>(IReadOnlyCollection<string> userIds, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>Runtime-typed variant (the payload's type carries <see cref="RealtimeEventAttribute"/>); used by the chat pipeline.</summary>
    Task SendToUsersAsync(IReadOnlyCollection<string> userIds, object payload, CancellationToken cancellationToken = default);

    /// <summary>Only for groups whose membership cannot go stale or whose content is not sensitive. Private data: use <see cref="PublishToGroupMembersAsync{TEvent}"/>.</summary>
    Task PublishGroupAsync<TEvent>(string group, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// Private group delivery: the CURRENT member list is asked from the host (<see cref="IRealtimeGroupMembership"/>) at publish time and
    /// the event is sent to those users only, so a connection that joined earlier but lost access receives nothing.
    /// </summary>
    Task PublishToGroupMembersAsync<TEvent>(string group, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}

/// <summary>Host-provided decision whether the authenticated user may join a group. Clients never grant themselves access.</summary>
public interface IRealtimeGroupAuthorizer
{
    ValueTask<bool> CanJoinAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken);
}

/// <summary>Host-provided CURRENT member user ids of a (private) group, read from the host's source of truth on every call. Never cache it.</summary>
public interface IRealtimeGroupMembership
{
    ValueTask<IReadOnlyCollection<string>> GetMemberUserIdsAsync(string group, CancellationToken cancellationToken);
}

/// <summary>
/// Call when access to a group was revoked: removes the user's live connections of THIS server instance from the group right away.
/// shortcut: the connection registry is per instance; with several instances rely on <see cref="IRealtimePublisher.PublishToGroupMembersAsync{TEvent}"/>
/// (membership is re-read on every publish) and the token-bound connection lifetime.
/// </summary>
public interface IRealtimeGroupManager
{
    Task RevokeAsync(string userId, string group, CancellationToken cancellationToken = default);

    /// <summary>Removes the user from every group (account suspended, role removed, ...).</summary>
    Task RevokeUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Default: no group can be joined until the host registers its own authorizer.</summary>
internal sealed class DenyAllGroupAuthorizer : IRealtimeGroupAuthorizer
{
    public ValueTask<bool> CanJoinAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    public string HubPath { get; set; } = "/hubs/realtime";

    /// <summary>JWT claim holding the stable user id used for user-targeted delivery.</summary>
    public string UserIdClaim { get; set; } = "sub";

    /// <summary>Optional SignalR Redis backplane (scale-out only; never a message store). Empty = in-process.</summary>
    public string? RedisConnectionString { get; set; }

    public int MaxGroupNameLength { get; set; } = 200;
}
