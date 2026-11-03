using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace OpsFlow.Realtime;

/// <summary>Carries "revalidate now" requests between server instances. Optional: without it only the local instance is asked.</summary>
internal interface IRevalidationBus
{
    /// <summary>Calls <paramref name="onRemoteRequest"/> whenever ANOTHER instance asks. The message carries no identity or data: it can only trigger a check.</summary>
    Task SubscribeAsync(Action onRemoteRequest, CancellationToken cancellationToken);

    Task PublishAsync(CancellationToken cancellationToken);
}

internal sealed partial class RealtimeRevalidation(
    RealtimeConnectionMonitor monitor,
    ILogger<RealtimeRevalidation> logger,
    IRevalidationBus? bus = null
) : IRealtimeConnectionRevalidation
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(2);

    public async ValueTask RequestAsync(CancellationToken cancellationToken = default)
    {
        monitor.RequestRun();
        if (bus is null)
        {
            return;
        }

        try
        {
            await bus.PublishAsync(cancellationToken).WaitAsync(PublishTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            LogPublishFailed(logger, ex); // best effort: the revocation is already committed and the periodic check still enforces it
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not ask other instances to revalidate realtime connections; the periodic check will"
    )]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Redis pub/sub over the SAME configuration (host, credentials, channel prefix) as the SignalR backplane, on its own channel name. The
/// backplane's connection is private to SignalR, so this opens one extra connection per instance, shared by every request. Not a message
/// store: a lost signal is covered by the periodic check.
/// </summary>
internal sealed class RedisRevalidationBus(IOptions<RedisOptions> redis)
    : IRevalidationBus,
        IAsyncDisposable
{
    // Distinct from every channel the SignalR backplane derives from the hub type name (all, group:*, user:*, internal:*).
    private static readonly RedisChannel Channel = RedisChannel.Literal(
        $"{typeof(RealtimeHub).FullName}:session-revalidation"
    );

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly object _lock = new();
    private Task<IConnectionMultiplexer>? _connection;

    public async Task SubscribeAsync(Action onRemoteRequest, CancellationToken cancellationToken)
    {
        var subscriber = (await ConnectedAsync(cancellationToken)).GetSubscriber();
        await subscriber.SubscribeAsync(
            Channel,
            (_, message) =>
            {
                if (message != _instanceId) // this instance already revalidated locally before publishing
                {
                    onRemoteRequest();
                }
            }
        );
    }

    public async Task PublishAsync(CancellationToken cancellationToken) =>
        await (await ConnectedAsync(cancellationToken))
            .GetSubscriber()
            .PublishAsync(Channel, _instanceId);

    private async Task<IConnectionMultiplexer> ConnectedAsync(CancellationToken cancellationToken)
    {
        Task<IConnectionMultiplexer> connecting;
        lock (_lock)
        {
            connecting = _connection ??= ConnectAsync();
        }

        return await connecting.WaitAsync(cancellationToken);
    }

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        try
        {
            var settings = redis.Value;
            return settings.ConnectionFactory is { } factory
                ? await factory(TextWriter.Null)
                : await ConnectionMultiplexer.ConnectAsync(settings.Configuration);
        }
        catch
        {
            lock (_lock)
            {
                _connection = null; // a failed attempt must not be cached forever
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IConnectionMultiplexer>? connection;
        lock (_lock)
        {
            (connection, _connection) = (_connection, null);
        }

        if (connection is { IsCompletedSuccessfully: true })
        {
            await connection.Result.DisposeAsync();
        }
    }
}
