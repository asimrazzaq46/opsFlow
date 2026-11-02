using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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

[RealtimeEvent("test.backplane")]
public sealed record BackplanePing(string Text);

/// <summary>
/// The SignalR Redis backplane with the StackExchange.Redis version the API actually ships (this project resolves the same one):
/// an event published on instance B reaches a client connected to instance A only through Redis. Needs OPSFLOW_TEST_REDIS
/// (e.g. 127.0.0.1:6380 from docker compose); a unique channel prefix isolates the run from any other backplane on that Redis.
/// </summary>
public sealed class RedisBackplaneTests
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

    /// <summary>Authenticates "Bearer userId" so the test exercises delivery, not token validation (covered elsewhere).</summary>
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

    private static Task<IHost> StartAsync(string redis) =>
        new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UserIdAuthHandler>("Test", _ => { });
                s.AddAuthorization();
                s.AddRealtime(o => o.RedisConnectionString = redis);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapRealtimeHub());
            })).StartAsync();

    private static HubConnection Connect(IHost host, string user, ConcurrentQueue<string> received)
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
        connection.On<string, JsonElement>("Receive", (name, payload) => received.Enqueue(payload.GetProperty("text").GetString()!));
        return connection;
    }

    /// <summary>Publishes from the other instance until the client sees it (a resubscribing backplane may drop the first attempts).</summary>
    private static async Task<bool> DeliveredAsync(IHost publisherHost, string user, string text, ConcurrentQueue<string> received)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            using (var scope = publisherHost.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IRealtimePublisher>().NotifyUserAsync(user, new BackplanePing(text), CancellationToken.None);
            }

            for (var i = 0; i < 20; i++)
            {
                if (received.Contains(text))
                {
                    return true;
                }

                await Task.Delay(25);
            }
        }

        return false;
    }

    [LiveRedisFact]
    public async Task Events_cross_instances_and_survive_backplane_and_client_reconnects()
    {
        Assert.Equal(3, typeof(ConnectionMultiplexer).Assembly.GetName().Version!.Major); // the version the API runs with

        var name = $"opsflow-backplane-test-{Guid.NewGuid():N}";
        var redis = $"{LiveRedis},channelPrefix={name},name={name}";
        using var a = await StartAsync(redis);
        using var b = await StartAsync(redis);
        var received = new ConcurrentQueue<string>();
        await using var client = Connect(a, "u1", received);
        await client.StartAsync();

        Assert.True(await DeliveredAsync(b, "u1", "first", received)); // B -> Redis -> A -> client

        // Drop ONLY this test's backplane connections (matched by client name); StackExchange.Redis must reconnect and resubscribe.
        using (var admin = await ConnectionMultiplexer.ConnectAsync($"{LiveRedis},allowAdmin=true"))
        {
            var server = admin.GetServers()[0];
            var killed = 0;
            foreach (var c in await server.ClientListAsync())
            {
                if (c.Name == name)
                {
                    await server.ExecuteAsync("CLIENT", "KILL", "ID", c.Id);
                    killed++;
                }
            }

            Assert.True(killed > 0, "no backplane connection found");
        }

        Assert.True(await DeliveredAsync(b, "u1", "after-redis-reconnect", received));

        await client.StopAsync();
        await client.StartAsync(); // client reconnect lands on a fresh connection of instance A
        Assert.True(await DeliveredAsync(b, "u1", "after-client-reconnect", received));

        await a.StopAsync();
        await b.StopAsync();
    }
}
