using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpsFlow.Realtime.Tests;

[RealtimeEvent("test.ping")]
public sealed record Ping(string Text);

public sealed record UnnamedEvent(string Text);

/// <summary>Token = "userId" or "userId||expSeconds"; the real host validates a JWT instead.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token) && Request.Headers.Authorization.ToString() is { Length: > 7 } header)
        {
            token = header[7..];
        }

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var parts = token.Split('|');
        List<Claim> claims = [new("sub", parts[0])];
        if (parts.Length > 2)
        {
            claims.Add(new Claim("exp", parts[2]));
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

/// <summary>Stand-in for the host's database: who may join / is currently a member of which group. Mutating it = access revoked or granted.</summary>
public sealed class MutableAccess : IRealtimeGroupAuthorizer, IRealtimeGroupMembership
{
    private readonly ConcurrentDictionary<(string User, string Group), bool> _allowed = new();

    public void Allow(string user, string group) => _allowed[(user, group)] = true;

    public void Revoke(string user, string group) => _allowed.TryRemove((user, group), out _);

    public ValueTask<bool> CanJoinAsync(ClaimsPrincipal user, string group, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_allowed.ContainsKey((user.FindFirst("sub")!.Value, group)));

    public ValueTask<IReadOnlyCollection<string>> GetMemberUserIdsAsync(string group, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>(_allowed.Keys.Where(k => k.Group == group).Select(k => k.User).ToList());
}

public sealed class HubTests : IAsyncLifetime
{
    private IHost _host = null!;
    private IServiceScope _scope = null!;
    private readonly MutableAccess _access = new();
    private TestServer Server => _host.GetTestServer();

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                s.AddAuthorization();
                s.AddRealtime();
                s.AddSingleton<IRealtimeGroupAuthorizer>(_access);
                s.AddSingleton<IRealtimeGroupMembership>(_access);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapRealtimeHub());
            })).StartAsync();
        _scope = _host.Services.CreateScope();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        _scope.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class Probe(HubConnection connection)
    {
        public ConcurrentQueue<(string Name, JsonElement Payload)> Events { get; } = new();

        public HubConnection Connection { get; } = connection;

        public async Task<bool> WaitAsync(int timeoutMs = 3000)
        {
            for (var waited = 0; waited < timeoutMs; waited += 25)
            {
                if (!Events.IsEmpty)
                {
                    return true;
                }

                await Task.Delay(25);
            }

            return false;
        }
    }

    private Probe Connect(string? token, TestServer? server = null)
    {
        server ??= Server;
        var connection = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, "/hubs/realtime"), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            o.WebSocketFactory = (ctx, ct) => new ValueTask<System.Net.WebSockets.WebSocket>(
                server.CreateWebSocketClient().ConnectAsync(token is null ? ctx.Uri : new Uri($"{ctx.Uri}&access_token={Uri.EscapeDataString(token)}"), ct));
            if (token is not null)
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }
        }).Build();
        var probe = new Probe(connection);
        connection.On<string, JsonElement>("Receive", (name, payload) => probe.Events.Enqueue((name, payload.Clone())));
        return probe;
    }

    private IRealtimePublisher Publisher => _scope.ServiceProvider.GetRequiredService<IRealtimePublisher>();

    private IRealtimeGroupManager Manager => _host.Services.GetRequiredService<IRealtimeGroupManager>();

    [Fact]
    public async Task Anonymous_connections_are_rejected_and_authenticated_ones_are_identified_by_the_validated_claim()
    {
        var anonymous = Connect(null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.Connection.StartAsync());

        var alice = Connect("alice");
        await alice.Connection.StartAsync();
        await Publisher.NotifyUserAsync("alice", new Ping("hi"));

        Assert.True(await alice.WaitAsync());
        Assert.Equal(("test.ping", "hi"), (alice.Events.Single().Name, alice.Events.Single().Payload.GetProperty("text").GetString()));
        await alice.Connection.DisposeAsync();
    }

    [Fact]
    public async Task User_notifications_reach_only_the_named_users()
    {
        var alice = Connect("alice");
        var bob = Connect("bob");
        var carol = Connect("carol");
        foreach (var p in new[] { alice, bob, carol })
        {
            await p.Connection.StartAsync();
        }

        await Publisher.NotifyUsersAsync(["alice", "bob"], new Ping("two"));

        Assert.True(await alice.WaitAsync());
        Assert.True(await bob.WaitAsync());
        await Task.Delay(200);
        Assert.Empty(carol.Events);
        foreach (var p in new[] { alice, bob, carol })
        {
            await p.Connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Group_joins_follow_the_host_authorizer_never_the_client_and_are_rechecked_on_every_join()
    {
        _access.Allow("alice", "room-1");
        var member = Connect("alice");
        var outsider = Connect("bob");
        await member.Connection.StartAsync();
        await outsider.Connection.StartAsync();

        Assert.True(await member.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));
        Assert.False(await outsider.Connection.InvokeAsync<bool>("JoinGroup", "room-1")); // knowing the name is not enough
        Assert.False(await member.Connection.InvokeAsync<bool>("JoinGroup", "room-2"));
        Assert.False(await member.Connection.InvokeAsync<bool>("JoinGroup", ""));
        Assert.False(await member.Connection.InvokeAsync<bool>("JoinGroup", new string('x', 1000)));

        await Publisher.PublishGroupAsync("room-1", new Ping("group"));
        Assert.True(await member.WaitAsync());
        await Task.Delay(200);
        Assert.Empty(outsider.Events);

        _access.Revoke("alice", "room-1"); // access removed in the host: a later join is refused
        Assert.False(await member.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));
        await member.Connection.InvokeAsync("LeaveGroup", "room-1");
        await member.Connection.DisposeAsync();
        await outsider.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Private_group_events_go_only_to_current_members_even_if_a_revoked_connection_is_still_in_the_group()
    {
        _access.Allow("alice", "room-1");
        _access.Allow("bob", "room-1");
        var alice = Connect("alice");
        var bob = Connect("bob");
        await alice.Connection.StartAsync();
        await bob.Connection.StartAsync();
        Assert.True(await alice.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));
        Assert.True(await bob.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));

        _access.Revoke("bob", "room-1"); // revoked in the host, but bob's connection never left the SignalR group
        await Publisher.PublishToGroupMembersAsync("room-1", new Ping("secret"));

        Assert.True(await alice.WaitAsync());
        await Task.Delay(200);
        Assert.Empty(bob.Events);

        // the plain group publish is documented as unsafe for private data: the stale connection still receives it
        await Publisher.PublishGroupAsync("room-1", new Ping("stale"));
        Assert.True(await bob.WaitAsync());
        await alice.Connection.DisposeAsync();
        await bob.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Revoking_evicts_live_connections_from_the_group_immediately()
    {
        _access.Allow("alice", "room-1");
        _access.Allow("bob", "room-1");
        _access.Allow("bob", "room-2");
        var alice = Connect("alice");
        var bob = Connect("bob");
        await alice.Connection.StartAsync();
        await bob.Connection.StartAsync();
        foreach (var (probe, room) in new[] { (alice, "room-1"), (bob, "room-1"), (bob, "room-2") })
        {
            Assert.True(await probe.Connection.InvokeAsync<bool>("JoinGroup", room));
        }

        await Manager.RevokeAsync("bob", "room-1");
        await Publisher.PublishGroupAsync("room-1", new Ping("after revoke"));
        Assert.True(await alice.WaitAsync());
        await Task.Delay(200);
        Assert.Empty(bob.Events);

        await Publisher.PublishGroupAsync("room-2", new Ping("other room"));
        Assert.True(await bob.WaitAsync()); // only the revoked group was affected

        await Manager.RevokeUserAsync("bob"); // e.g. account suspended
        var seen = bob.Events.Count;
        await Publisher.PublishGroupAsync("room-2", new Ping("after user revoke"));
        await Task.Delay(300);
        Assert.Equal(seen, bob.Events.Count);
        await alice.Connection.DisposeAsync();
        await bob.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Group_membership_does_not_survive_a_new_connection_so_clients_must_rejoin()
    {
        _access.Allow("bob", "room-1");
        var first = Connect("bob");
        await first.Connection.StartAsync();
        Assert.True(await first.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));
        await first.Connection.DisposeAsync();

        var second = Connect("bob"); // a reconnect creates a NEW server connection
        await second.Connection.StartAsync();
        await Publisher.PublishGroupAsync("room-1", new Ping("before rejoin"));
        await Task.Delay(300);
        Assert.Empty(second.Events);

        Assert.True(await second.Connection.InvokeAsync<bool>("JoinGroup", "room-1"));
        await Publisher.PublishGroupAsync("room-1", new Ping("after rejoin"));
        Assert.True(await second.WaitAsync());
        await second.Connection.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_host_authorizer_no_group_can_be_joined()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                s.AddAuthorization();
                s.AddRealtime();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapRealtimeHub());
            })).StartAsync();
        var alice = Connect("alice", host.GetTestServer());
        await alice.Connection.StartAsync();

        Assert.False(await alice.Connection.InvokeAsync<bool>("JoinGroup", "anything"));
        await alice.Connection.DisposeAsync();
    }

    [Fact]
    public async Task A_connection_ends_when_its_token_expires_so_the_client_must_reauthenticate()
    {
        var expiresSoon = Connect($"alice||{DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds()}");
        var closed = new TaskCompletionSource();
        expiresSoon.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await expiresSoon.Connection.StartAsync();

        var winner = await Task.WhenAny(closed.Task, Task.Delay(5000));

        Assert.Same(closed.Task, winner);
        await expiresSoon.Connection.DisposeAsync();
    }

    // ---------------------------------------------------------------- DI / configuration

    private sealed class HostUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection) => "host";
    }

    [Fact]
    public void A_host_registered_user_id_provider_is_kept_and_the_default_is_the_claim_provider()
    {
        var custom = new ServiceCollection();
        custom.AddSingleton<IUserIdProvider, HostUserIdProvider>();
        custom.AddRealtime();
        Assert.IsType<HostUserIdProvider>(custom.BuildServiceProvider().GetRequiredService<IUserIdProvider>());

        var plain = new ServiceCollection();
        plain.AddRealtime();
        Assert.Equal("ClaimUserIdProvider", plain.BuildServiceProvider().GetRequiredService<IUserIdProvider>().GetType().Name);
    }

    [Theory]
    [InlineData("hubs/realtime", "sub", 200)]
    [InlineData("/", "sub", 200)]
    [InlineData("/hub", " ", 200)]
    [InlineData("/hub", "sub", 0)]
    public void Invalid_options_fail_validation(string hubPath, string claim, int maxGroupName)
    {
        var services = new ServiceCollection();
        services.AddRealtime(o =>
        {
            o.HubPath = hubPath;
            o.UserIdClaim = claim;
            o.MaxGroupNameLength = maxGroupName;
        });

        Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().GetRequiredService<IOptions<RealtimeOptions>>().Value);
    }

    [Fact]
    public async Task Mapping_the_hub_without_authentication_fails_with_a_clear_message()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var host = await new HostBuilder().ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddRealtime();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapRealtimeHub());
                })).StartAsync();
        });
        Assert.Contains("authentication", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
