using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpsFlow.Realtime;

/// <summary>
/// Authenticated hub. A connection lives no longer than its access token: it is aborted at expiry so the client reconnects with a fresh
/// (and re-validated) token - a revoked session cannot reconnect. Group joins are decided by the host's <see cref="IRealtimeGroupAuthorizer"/>.
/// </summary>
[Authorize]
public sealed partial class RealtimeHub(
    IRealtimeGroupAuthorizer authorizer,
    RealtimeConnectionRegistry registry,
    IOptions<RealtimeOptions> options,
    TimeProvider clock,
    ILogger<RealtimeHub> logger
) : Hub<IRealtimeClient>
{
    public override Task OnConnectedAsync()
    {
        LogConnected(logger, Context.UserIdentifier);
        registry.Add(Context.ConnectionId, Context.UserIdentifier);
        if (Context.User?.FindFirst("exp")?.Value is { } exp && long.TryParse(exp, out var seconds))
        {
            var remaining = DateTimeOffset.FromUnixTimeSeconds(seconds) - clock.GetUtcNow();
            _ = AbortAtExpiryAsync(Context, remaining, clock);
        }

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        LogDisconnected(logger, Context.UserIdentifier, exception is null ? "closed" : "faulted");
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Returns false (and joins nothing) unless the host authorizer allows this user right now.</summary>
    public async Task<bool> JoinGroup(string group)
    {
        if (
            string.IsNullOrWhiteSpace(group)
            || group.Length > options.Value.MaxGroupNameLength
            || Context.User is null
            || !await authorizer.CanJoinAsync(Context.User, group, Context.ConnectionAborted)
        )
        {
            LogJoinDenied(logger, Context.UserIdentifier);
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        registry.Joined(Context.ConnectionId, group);
        return true;
    }

    public async Task LeaveGroup(string group)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        registry.Left(Context.ConnectionId, group);
    }

    private static async Task AbortAtExpiryAsync(
        HubCallerContext context,
        TimeSpan remaining,
        TimeProvider clock
    )
    {
        try
        {
            await Task.Delay(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                clock,
                context.ConnectionAborted
            );
            context.Abort();
        }
        catch (OperationCanceledException)
        {
            // connection ended first
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Realtime connected user {UserId}")]
    private static partial void LogConnected(ILogger logger, string? userId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Realtime disconnected user {UserId} ({Reason})"
    )]
    private static partial void LogDisconnected(ILogger logger, string? userId, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Realtime group join denied for user {UserId}"
    )]
    private static partial void LogJoinDenied(ILogger logger, string? userId);
}
