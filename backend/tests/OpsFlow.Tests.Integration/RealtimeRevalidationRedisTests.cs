using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Realtime;
using StackExchange.Redis;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// "Revalidate now" across server instances over Redis. The periodic check is set to one hour, so a connection can only be closed here
/// because the signal crossed Redis. Needs OPSFLOW_TEST_REDIS (e.g. 127.0.0.1:6380 from docker compose); every test uses a unique channel
/// prefix, like the backplane test, so it cannot touch any other data on that Redis.
/// </summary>
public sealed class RealtimeRevalidationRedisTests
{
    private static readonly string? LiveRedis = Environment.GetEnvironmentVariable("OPSFLOW_TEST_REDIS");

    private sealed class LiveRedisFactAttribute : FactAttribute
    {
        public LiveRedisFactAttribute()
        {
            if (string.IsNullOrEmpty(LiveRedis))
            {
                Skip = "Set OPSFLOW_TEST_REDIS to run against a real Redis.";
            }
        }
    }

    /// <summary>Stands in for the session database shared by every instance: a user in <see cref="Revoked"/> is no longer valid anywhere.</summary>
    private sealed class SharedDirectory : IRealtimeConnectionValidator
    {
        public ConcurrentDictionary<string, bool> Revoked { get; } = new();

        public Task<IReadOnlySet<string>> FindValidAsync(IReadOnlyList<RealtimeConnectionInfo> connections, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(connections
                .Where(c => !Revoked.ContainsKey(c.User.FindFirst("sub")!.Value))
                .Select(c => c.ConnectionId)
                .ToHashSet());
    }

    private sealed class UserIdAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var user = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : Request.Query["access_token"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(user)
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")), "Test")));
        }
    }

    private static Task<IHost> StartAsync(string redis, SharedDirectory directory) =>
        new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserIdAuthHandler>("Test", _ => { });
                s.AddAuthorization();
                s.AddRealtime(o =>
                {
                    o.RedisConnectionString = redis;
                    o.RevalidationInterval = TimeSpan.FromHours(1);
                    o.MaxUnverifiedAge = TimeSpan.FromHours(2);
                });
                s.AddSingleton<IRealtimeConnectionValidator>(directory);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapRealtimeHub());
            })).StartAsync();

    private static async Task<HubConnection> ConnectAsync(IHost host, string user)
    {
        var server = host.GetTestServer();
        var connection = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, "/hubs/realtime"), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            o.WebSocketFactory = (ctx, ct) => new ValueTask<System.Net.WebSockets.WebSocket>(
                server.CreateWebSocketClient().ConnectAsync(new Uri($"{ctx.Uri}&access_token={user}"), ct));
            o.AccessTokenProvider = () => Task.FromResult<string?>(user);
        }).Build();
        await connection.StartAsync();
        return connection;
    }

    private static Task RequestAsync(IHost host) =>
        host.Services.GetRequiredService<IRealtimeConnectionRevalidation>().RequestAsync().AsTask();

    /// <summary>Re-sends the request until the connection closes: a just-subscribed Redis channel may drop the very first publishes.</summary>
    private static async Task<bool> ClosedAfterRequestingAsync(IHost requester, HubConnection connection, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            await RequestAsync(requester);
            for (var i = 0; i < 20; i++)
            {
                if (connection.State == HubConnectionState.Disconnected)
                {
                    return true;
                }

                await Task.Delay(25);
            }
        }

        return false;
    }

    private static string UniqueRedis(out string name)
    {
        name = $"opsflow-revalidation-test-{Guid.NewGuid():N}";
        return $"{LiveRedis},channelPrefix={name},name={name}";
    }

    [LiveRedisFact]
    public async Task A_revocation_request_on_one_instance_closes_the_connection_held_by_another_instance()
    {
        var redis = UniqueRedis(out _);
        var directory = new SharedDirectory();
        using var a = await StartAsync(redis, directory);
        using var b = await StartAsync(redis, directory);
        await using var revoked = await ConnectAsync(a, "revoked-user");
        await using var bystander = await ConnectAsync(a, "other-user");
        directory.Revoked["revoked-user"] = true; // committed in the shared source of truth; B served the request that revoked it

        Assert.True(await ClosedAfterRequestingAsync(b, revoked)); // B -> Redis -> A -> A revalidates its own connections

        Assert.Equal(HubConnectionState.Connected, bystander.State);
    }

    [LiveRedisFact]
    public async Task Instances_in_another_redis_namespace_are_not_triggered()
    {
        var directory = new SharedDirectory();
        using var ours = await StartAsync(UniqueRedis(out _), directory);
        using var requester = await StartAsync(UniqueRedis(out _), directory); // a different prefix: a different deployment
        await using var connection = await ConnectAsync(ours, "revoked-user");
        directory.Revoked["revoked-user"] = true;

        for (var i = 0; i < 6; i++)
        {
            await RequestAsync(requester);
            await Task.Delay(250);
        }

        Assert.Equal(HubConnectionState.Connected, connection.State); // the signal never crossed the namespace

        Assert.True(await ClosedAfterRequestingAsync(ours, connection)); // its own request (local trigger) still works
    }

    [LiveRedisFact]
    public async Task Redis_connections_and_subscriptions_are_released_when_the_host_stops()
    {
        var redis = UniqueRedis(out var name);
        using var admin = await ConnectionMultiplexer.ConnectAsync($"{LiveRedis},allowAdmin=true");
        var server = admin.GetServers()[0];
        async Task<int> OpenClientsAsync() => (await server.ClientListAsync()).Count(c => c.Name == name);

        var host = await StartAsync(redis, new SharedDirectory());
        try
        {
            await using var client = await ConnectAsync(host, "u1"); // the backplane connects with the first client; the revalidation bus at start
            var during = 0;
            for (var i = 0; i < 100 && during < 2; i++)
            {
                during = await OpenClientsAsync();
                await Task.Delay(50);
            }

            Assert.True(during >= 2, $"expected the backplane and the revalidation connection, saw {during}");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }

        var after = -1;
        for (var i = 0; i < 100 && after != 0; i++)
        {
            after = await OpenClientsAsync();
            await Task.Delay(100);
        }

        Assert.Equal(0, after); // nothing of this instance is left on Redis
    }
}
