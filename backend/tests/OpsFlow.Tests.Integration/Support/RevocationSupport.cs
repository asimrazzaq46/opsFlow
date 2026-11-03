using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Realtime;

namespace OpsFlow.Tests.Integration.Support;

[RealtimeEvent("test.revocation-ping")]
public sealed record RevocationPing(string Text);

/// <summary>Switches the live-connection validator of a running host between "healthy" and "dependency down".</summary>
public sealed class ValidationControl
{
    private volatile bool _failing;

    public bool Failing
    {
        get => _failing;
        set => _failing = value;
    }
}

/// <summary>Only the post-commit fast path can close connections here: the periodic check is effectively off (30 minutes).</summary>
public sealed class FastPathOnlyApiFactory : ApiFactory
{
    protected override string RevalidationInterval => "00:30:00";

    protected override string MaxUnverifiedAge => "01:00:00";
}

/// <summary>Short intervals so the periodic check and the fail-closed rule can be observed in seconds, with a validator that can be made to fail.</summary>
public sealed class PeriodicRevalidationApiFactory : ApiFactory
{
    public ValidationControl Validation { get; } = new();

    protected override string RevalidationInterval => "00:00:00.200";

    protected override string MaxUnverifiedAge => "00:00:02";

    protected override string ValidationTimeout => "00:00:00.500";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Wraps the real validator: the production rule still decides, the test can only make the dependency "fail".
            var real = services.Single(d => d.ServiceType == typeof(IRealtimeConnectionValidator)).ImplementationType!;
            services.RemoveAll<IRealtimeConnectionValidator>();
            services.AddScoped<IRealtimeConnectionValidator>(sp => new SwitchableValidator(
                Validation,
                (IRealtimeConnectionValidator)ActivatorUtilities.CreateInstance(sp, real)
            ));
        });
    }

    private sealed class SwitchableValidator(ValidationControl control, IRealtimeConnectionValidator inner)
        : IRealtimeConnectionValidator
    {
        public Task<IReadOnlySet<string>> FindValidAsync(IReadOnlyList<RealtimeConnectionInfo> connections, CancellationToken cancellationToken) =>
            control.Failing
                ? throw new InvalidOperationException("simulated validation dependency outage")
                : inner.FindValidAsync(connections, cancellationToken);
    }
}

public static class RevocationAsserts
{
    /// <summary>True once the server has closed the connection (polled; the bound is generous for CI, the healthy-system latency is milliseconds).</summary>
    public static async Task<bool> ClosedAsync(HubClient hub, int timeoutMs = 8000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (hub.Connection.State == HubConnectionState.Disconnected)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>True when the connection is still open after <paramref name="forMs"/> (used to prove a valid session is left alone).</summary>
    public static async Task<bool> StaysConnectedAsync(HubClient hub, int forMs = 1000)
    {
        await Task.Delay(forMs);
        return hub.Connection.State == HubConnectionState.Connected;
    }

    public static async Task PublishAsync(ApiFactory factory, Guid userId, string text)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRealtimePublisher>().NotifyUserAsync(userId.ToString(), new RevocationPing(text));
    }
}
