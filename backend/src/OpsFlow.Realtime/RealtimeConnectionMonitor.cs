using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpsFlow.Realtime;

/// <summary>
/// Keeps live connections honest: every <see cref="RealtimeOptions.RevalidationInterval"/> (and whenever a revalidation is requested) the host's
/// <see cref="IRealtimeConnectionValidator"/> is asked which of this instance's connections are still valid, and the others are closed.
/// Fail closed: a connection that could not be verified for <see cref="RealtimeOptions.MaxUnverifiedAge"/> (validator down, timing out or
/// throwing) is closed too, so an outage never lets unverified connections receive events indefinitely. Each instance checks only its own
/// connections against the host's source of truth, so no coordination between instances is needed.
/// Without a registered validator the monitor does nothing and connections end at token expiry only.
/// </summary>
internal sealed partial class RealtimeConnectionMonitor(
    RealtimeConnectionRegistry registry,
    IServiceScopeFactory scopes,
    IOptions<RealtimeOptions> options,
    TimeProvider clock,
    ILogger<RealtimeConnectionMonitor> logger,
    IRevalidationBus? bus = null
) : BackgroundService
{
    // Capacity 1: a burst of revocations collapses into one pending run, and a request that arrives while a run is in progress
    // still triggers the next one immediately, so a revocation committed before the request is always seen by a later run.
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }
    );

    internal void RequestRun() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var probe = scopes.CreateScope())
        {
            if (probe.ServiceProvider.GetService<IRealtimeConnectionValidator>() is null)
            {
                LogDisabled(logger);
                return;
            }
        }

        var subscribed = bus is null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!subscribed)
                {
                    subscribed = await TrySubscribeAsync(stoppingToken);
                }

                await WaitForNextRunAsync(stoppingToken);
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogCycleFailed(logger, ex); // the loop must survive anything: it is what enforces revocation
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One revalidation pass over this instance's connections (internal so tests can drive it deterministically).</summary>
    internal async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var connections = registry.Snapshot();
        if (connections.Count == 0)
        {
            return;
        }

        HashSet<string> closed = [];
        int invalid = 0,
            failedBatches = 0;
        foreach (var batch in connections.Chunk(settings.ValidationBatchSize))
        {
            // Taken BEFORE asking: a revocation that lands while the check runs must not be covered by this verification.
            var checkedAt = clock.GetUtcNow();
            try
            {
                var valid = await FindValidAsync(batch, settings, stoppingToken);
                foreach (var connection in batch)
                {
                    if (valid.Contains(connection.ConnectionId))
                    {
                        registry.MarkVerified(connection.ConnectionId, checkedAt);
                    }
                    else if (closed.Add(connection.ConnectionId))
                    {
                        invalid++;
                        Abort(connection);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedBatches++;
                LogValidationFailed(logger, ex, batch.Length);
            }
        }

        var cutoff = clock.GetUtcNow() - settings.MaxUnverifiedAge;
        var unverified = 0;
        foreach (var connection in registry.Snapshot())
        {
            if (connection.VerifiedAt <= cutoff && closed.Add(connection.ConnectionId))
            {
                unverified++;
                Abort(connection);
            }
        }

        if (invalid + unverified + failedBatches > 0)
        {
            LogCycle(logger, connections.Count, invalid, unverified, failedBatches);
        }
    }

    private async Task<IReadOnlySet<string>> FindValidAsync(
        RealtimeConnectionRegistry.Tracked[] batch,
        RealtimeOptions settings,
        CancellationToken stoppingToken
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRealtimeConnectionValidator>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            // WaitAsync bounds a validator that ignores its token; the token lets a cooperative one stop working.
            return await validator
                .FindValidAsync(
                    [.. batch.Select(c => new RealtimeConnectionInfo(c.ConnectionId, c.User))],
                    cancellation.Token
                )
                .WaitAsync(settings.ValidationTimeout, clock, stoppingToken);
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync();
            throw;
        }
    }

    private async Task WaitForNextRunAsync(CancellationToken stoppingToken)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(options.Value.RevalidationInterval, clock, wake.Token);
        var request = _requests.Reader.WaitToReadAsync(wake.Token).AsTask();
        await Task.WhenAny(timer, request);
        await wake.CancelAsync();
        _requests.Reader.TryRead(out _); // consumed before the run starts, so the run covers it
        await Task.WhenAll(Quiet(timer), Quiet(request));
        stoppingToken.ThrowIfCancellationRequested();
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // the losing side of the wait
        }
    }

    private async Task<bool> TrySubscribeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await bus!.SubscribeAsync(RequestRun, stoppingToken);
            return true;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogSubscribeFailed(logger, ex); // retried on the next pass; the periodic check works without it
            return false;
        }
    }

    // Abort is safe outside a hub invocation and idempotent; a connection that is already gone must not break the pass.
    private void Abort(RealtimeConnectionRegistry.Tracked connection)
    {
        try
        {
            connection.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            LogAbortSkipped(logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No IRealtimeConnectionValidator is registered: realtime connections end at token expiry only"
    )]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Realtime revalidation checked {Connections} connection(s): {Invalid} closed as invalid, {Unverified} closed as unverified, {FailedBatches} batch(es) failed"
    )]
    private static partial void LogCycle(
        ILogger logger,
        int connections,
        int invalid,
        int unverified,
        int failedBatches
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Realtime revalidation failed for a batch of {Connections} connection(s); they stay open until they exceed the maximum unverified age"
    )]
    private static partial void LogValidationFailed(
        ILogger logger,
        Exception exception,
        int connections
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "Realtime revalidation pass failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Realtime revalidation signal subscription failed; relying on the periodic check"
    )]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Closing a realtime connection that already ended"
    )]
    private static partial void LogAbortSkipped(ILogger logger, Exception exception);
}
