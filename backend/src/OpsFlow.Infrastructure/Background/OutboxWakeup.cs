using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Outbox;

namespace OpsFlow.Infrastructure.Background;

/// <summary>
/// Runs the outbox dispatch right after a commit signals new events, so live chat does not wait for the recurring schedule. Signals that
/// arrive while a run is in progress collapse into one follow-up run. Claims are atomic, so running next to the scheduled job is safe.
/// </summary>
public sealed partial class OutboxWakeup(IServiceScopeFactory scopes, ILogger<OutboxWakeup> logger)
    : BackgroundService,
        IOutboxSignal
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }
    );

    public void Notify() => _signals.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatchJob>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex); // the scheduled run retries whatever this run did not finish
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Immediate outbox dispatch failed; the scheduled run will retry")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
