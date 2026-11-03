using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace OpsFlow.Realtime.Tests;

/// <summary>Only <see cref="GetUtcNow"/> is controlled; timers stay real so timeouts still fire.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class ScriptedValidator : IRealtimeConnectionValidator
{
    private readonly List<int> _batches = [];

    public Func<IReadOnlyList<RealtimeConnectionInfo>, CancellationToken, Task<IReadOnlySet<string>>> Handler { get; set; } =
        (connections, _) => Task.FromResult<IReadOnlySet<string>>(connections.Select(c => c.ConnectionId).ToHashSet());

    public IReadOnlyList<int> Batches
    {
        get
        {
            lock (_batches)
            {
                return [.. _batches];
            }
        }
    }

    public Task<IReadOnlySet<string>> FindValidAsync(IReadOnlyList<RealtimeConnectionInfo> connections, CancellationToken cancellationToken)
    {
        lock (_batches)
        {
            _batches.Add(connections.Count);
        }

        return Handler(connections, cancellationToken);
    }

    public static Func<IReadOnlyList<RealtimeConnectionInfo>, CancellationToken, Task<IReadOnlySet<string>>> OnlyValid(params string[] ids) =>
        (_, _) => Task.FromResult<IReadOnlySet<string>>(ids.ToHashSet());

    public static Task<IReadOnlySet<string>> Down(IReadOnlyList<RealtimeConnectionInfo> _, CancellationToken __) =>
        throw new InvalidOperationException("source of truth unreachable");
}

public sealed class RevalidationTests
{
    private sealed class FakeConnection
    {
        private int _aborts;

        public CancellationTokenSource Ended { get; } = new();

        public int Aborts => Volatile.Read(ref _aborts);

        public Action Abort => () => Interlocked.Increment(ref _aborts);
    }

    private sealed class Harness
    {
        public Harness(RealtimeOptions? options = null, bool withValidator = true, IRevalidationBus? bus = null)
        {
            Options = options ?? new RealtimeOptions();
            var services = new ServiceCollection();
            if (withValidator)
            {
                services.AddSingleton<IRealtimeConnectionValidator>(Validator);
            }

            Monitor = new RealtimeConnectionMonitor(
                Registry,
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(Options),
                Clock,
                NullLogger<RealtimeConnectionMonitor>.Instance,
                bus
            );
        }

        public RealtimeOptions Options { get; }

        public RealtimeConnectionRegistry Registry { get; } = new();

        public ManualClock Clock { get; } = new();

        public ScriptedValidator Validator { get; } = new();

        public RealtimeConnectionMonitor Monitor { get; }

        public FakeConnection Connect(string id, string user = "u")
        {
            var connection = new FakeConnection();
            Registry.Add(
                id,
                user,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
                connection.Abort,
                Clock.GetUtcNow(),
                connection.Ended.Token
            );
            return connection;
        }
    }

    [Fact]
    public async Task Connections_the_validator_does_not_confirm_are_closed_and_confirmed_ones_are_left_alone()
    {
        var h = new Harness();
        var good = h.Connect("good");
        var revoked = h.Connect("revoked");
        h.Validator.Handler = ScriptedValidator.OnlyValid("good");

        await h.Monitor.RunCycleAsync(CancellationToken.None);
        await h.Monitor.RunCycleAsync(CancellationToken.None); // a closed connection is not closed again once it has ended

        Assert.Equal(0, good.Aborts);
        Assert.True(revoked.Aborts >= 1);
    }

    [Fact]
    public async Task A_validator_that_returns_nothing_fails_closed()
    {
        var h = new Harness();
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Validator.Handler = ScriptedValidator.OnlyValid();

        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, a.Aborts);
        Assert.Equal(1, b.Aborts);
    }

    [Fact]
    public async Task One_temporary_validation_failure_does_not_close_a_connection()
    {
        var h = new Harness(); // MaxUnverifiedAge 90 s
        var connection = h.Connect("a");
        h.Validator.Handler = ScriptedValidator.Down;

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, connection.Aborts);
    }

    [Fact]
    public async Task Repeated_failures_close_the_connection_once_the_maximum_unverified_age_is_reached()
    {
        var h = new Harness();
        var connection = h.Connect("a");
        h.Validator.Handler = ScriptedValidator.Down;

        foreach (var seconds in new[] { 30, 30, 29 }) // 89 s since it was last verified
        {
            h.Clock.Advance(TimeSpan.FromSeconds(seconds));
            await h.Monitor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(0, connection.Aborts);
        }

        h.Clock.Advance(TimeSpan.FromSeconds(1)); // 90 s
        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, connection.Aborts);
    }

    [Fact]
    public async Task A_successful_check_restarts_the_unverified_clock()
    {
        var h = new Harness();
        var connection = h.Connect("a");

        h.Validator.Handler = ScriptedValidator.Down;
        h.Clock.Advance(TimeSpan.FromSeconds(80));
        await h.Monitor.RunCycleAsync(CancellationToken.None);

        h.Validator.Handler = ScriptedValidator.OnlyValid("a"); // verified at +80 s
        await h.Monitor.RunCycleAsync(CancellationToken.None);

        h.Validator.Handler = ScriptedValidator.Down;
        h.Clock.Advance(TimeSpan.FromSeconds(80)); // 80 s since the last success, not 160 s
        await h.Monitor.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, connection.Aborts);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.Monitor.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, connection.Aborts);
    }

    [Fact]
    public async Task A_validator_that_hangs_is_cut_off_by_the_timeout_and_counts_as_a_failure()
    {
        var h = new Harness(new RealtimeOptions { ValidationTimeout = TimeSpan.FromMilliseconds(150) });
        var connection = h.Connect("a");
        h.Validator.Handler = async (_, _) =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None); // ignores its token on purpose
            return new HashSet<string>();
        };

        var watch = Stopwatch.StartNew();
        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "the pass must not wait for a hung validator");
        Assert.Equal(0, connection.Aborts); // a hung check is a failure, not a verdict: only the unverified age closes it
    }

    [Fact]
    public async Task Connections_are_validated_in_batches_of_the_configured_size()
    {
        var h = new Harness(new RealtimeOptions { ValidationBatchSize = 2 });
        for (var i = 0; i < 5; i++)
        {
            h.Connect($"c{i}");
        }

        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal([2, 2, 1], h.Validator.Batches.Order().Reverse());
    }

    [Fact]
    public async Task Ended_connections_are_dropped_from_the_registry_and_never_validated()
    {
        var h = new Harness();
        var gone = h.Connect("gone");
        h.Connect("alive");
        await gone.Ended.CancelAsync(); // a disconnect callback that never ran

        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal([1], h.Validator.Batches);
        Assert.Equal(1, h.Registry.Count);
    }

    [Fact]
    public async Task Without_connections_the_validator_is_not_called()
    {
        var h = new Harness();

        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Empty(h.Validator.Batches);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_aborted_does_not_stop_the_pass()
    {
        var h = new Harness();
        var second = h.Connect("second");
        h.Registry.Add("first", "u", new ClaimsPrincipal(), () => throw new ObjectDisposedException("connection"), h.Clock.GetUtcNow(), CancellationToken.None);
        h.Validator.Handler = ScriptedValidator.OnlyValid();

        await h.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, second.Aborts);
    }

    [Fact]
    public async Task Shutdown_cancels_a_running_pass_without_closing_anything()
    {
        var h = new Harness();
        var connection = h.Connect("a");
        using var stop = new CancellationTokenSource();
        h.Validator.Handler = async (_, ct) =>
        {
            await stop.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new HashSet<string>();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Monitor.RunCycleAsync(stop.Token));

        Assert.Equal(0, connection.Aborts);
    }

    [Fact]
    public async Task Marking_verification_and_removal_are_safe_for_unknown_and_concurrently_closed_connections()
    {
        var registry = new RealtimeConnectionRegistry();
        registry.MarkVerified("unknown", DateTimeOffset.UtcNow);
        registry.Remove("unknown");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var id = $"{worker}-{i}";
                registry.Add(id, "u", new ClaimsPrincipal(), () => { }, DateTimeOffset.UtcNow, CancellationToken.None);
                _ = registry.Snapshot();
                registry.MarkVerified(id, DateTimeOffset.UtcNow);
                registry.Remove(id);
            }
        })));

        Assert.Equal(0, registry.Count); // nothing leaks
    }

    [Fact]
    public async Task A_request_triggers_an_immediate_pass_and_bursts_are_coalesced()
    {
        var h = new Harness(new RealtimeOptions { RevalidationInterval = TimeSpan.FromHours(1), MaxUnverifiedAge = TimeSpan.FromHours(2) });
        var connection = h.Connect("a");
        h.Validator.Handler = ScriptedValidator.OnlyValid();
        await h.Monitor.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 50; i++)
            {
                h.Monitor.RequestRun();
            }

            for (var waited = 0; waited < 5000 && connection.Aborts == 0; waited += 25)
            {
                await Task.Delay(25);
            }

            Assert.True(connection.Aborts >= 1);
            await Task.Delay(200);
            Assert.True(h.Validator.Batches.Count <= 2, $"50 requests must not cause 50 passes, got {h.Validator.Batches.Count}");
        }
        finally
        {
            await h.Monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_monitor_stays_dormant_when_the_host_registered_no_validator()
    {
        var h = new Harness(withValidator: false);
        var connection = h.Connect("a");
        await h.Monitor.StartAsync(CancellationToken.None);

        h.Monitor.RequestRun();
        await Task.Delay(200);
        await h.Monitor.StopAsync(CancellationToken.None);

        Assert.Equal(0, connection.Aborts);
    }

    [Fact]
    public async Task A_connection_that_registers_after_a_request_pass_is_closed_by_the_next_pass_and_never_survives_beyond_it()
    {
        // The known, bounded window: token validated -> revoke commits and its pass runs -> connection registers. The pass cannot see it.
        var h = new Harness();
        h.Validator.Handler = ScriptedValidator.OnlyValid();
        h.Monitor.RequestRun();
        await h.Monitor.RunCycleAsync(CancellationToken.None); // the request pass: nothing registered yet
        var late = h.Connect("late");
        Assert.Equal(0, late.Aborts); // the interval between the request and the next pass is real and documented

        await h.Monitor.RunCycleAsync(CancellationToken.None); // the next periodic pass (at most RevalidationInterval later)

        Assert.Equal(1, late.Aborts);
    }

    [Fact]
    public async Task An_unreachable_redis_never_blocks_a_request_and_the_local_pass_still_runs()
    {
        var redis = new RedisOptions
        {
            Configuration = ConfigurationOptions.Parse("127.0.0.1:1,abortConnect=false,connectTimeout=300,syncTimeout=300,asyncTimeout=300"),
        };
        await using var bus = new RedisRevalidationBus(Microsoft.Extensions.Options.Options.Create(redis));
        var h = new Harness(new RealtimeOptions { RevalidationInterval = TimeSpan.FromHours(1), MaxUnverifiedAge = TimeSpan.FromHours(2) }, bus: bus);
        var connection = h.Connect("revoked");
        h.Validator.Handler = ScriptedValidator.OnlyValid();
        var revalidation = new RealtimeRevalidation(h.Monitor, NullLogger<RealtimeRevalidation>.Instance, bus);
        await h.Monitor.StartAsync(CancellationToken.None);
        try
        {
            var watch = Stopwatch.StartNew();
            await revalidation.RequestAsync(); // must neither throw nor hang
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");

            for (var waited = 0; waited < 5000 && connection.Aborts == 0; waited += 25)
            {
                await Task.Delay(25);
            }

            Assert.True(connection.Aborts >= 1); // the periodic/local path does not depend on Redis
        }
        finally
        {
            await h.Monitor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeBus : IRevalidationBus
    {
        private int _published;
        private int _subscribeAttempts;

        public Action? OnRemote { get; private set; }

        public bool FailPublish { get; set; }

        public bool HangPublish { get; set; }

        public bool FailFirstSubscribe { get; set; }

        public int Published => Volatile.Read(ref _published);

        public int SubscribeAttempts => Volatile.Read(ref _subscribeAttempts);

        public Task SubscribeAsync(Action onRemoteRequest, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _subscribeAttempts) == 1 && FailFirstSubscribe)
            {
                throw new InvalidOperationException("redis down");
            }

            OnRemote = onRemoteRequest;
            return Task.CompletedTask;
        }

        public async Task PublishAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _published);
            if (HangPublish)
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }

            if (FailPublish)
            {
                throw new InvalidOperationException("redis down");
            }
        }
    }

    [Fact]
    public async Task A_failing_or_hanging_redis_publish_never_breaks_a_request_and_the_local_pass_still_runs()
    {
        var h = new Harness(new RealtimeOptions { RevalidationInterval = TimeSpan.FromHours(1), MaxUnverifiedAge = TimeSpan.FromHours(2) });
        var connection = h.Connect("revoked");
        h.Validator.Handler = ScriptedValidator.OnlyValid();
        var bus = new FakeBus { FailPublish = true };
        var revalidation = new RealtimeRevalidation(h.Monitor, NullLogger<RealtimeRevalidation>.Instance, bus);
        await h.Monitor.StartAsync(CancellationToken.None);
        try
        {
            await revalidation.RequestAsync(); // the publish throws: swallowed

            for (var waited = 0; waited < 5000 && connection.Aborts == 0; waited += 25)
            {
                await Task.Delay(25);
            }

            Assert.True(connection.Aborts >= 1);
            Assert.Equal(1, bus.Published);

            bus.FailPublish = false;
            bus.HangPublish = true;
            var watch = Stopwatch.StartNew();
            await revalidation.RequestAsync(); // the publish hangs: bounded, then swallowed
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            await h.Monitor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_signal_from_another_instance_triggers_a_pass_and_a_failed_subscription_is_retried()
    {
        var bus = new FakeBus { FailFirstSubscribe = true };
        var h = new Harness(new RealtimeOptions { RevalidationInterval = TimeSpan.FromHours(1), MaxUnverifiedAge = TimeSpan.FromHours(2) }, bus: bus);
        var connection = h.Connect("revoked");
        h.Validator.Handler = ScriptedValidator.OnlyValid();
        await h.Monitor.StartAsync(CancellationToken.None);
        try
        {
            h.Monitor.RequestRun(); // forces the loop around once so the failed subscription is attempted again
            for (var waited = 0; waited < 5000 && bus.OnRemote is null; waited += 25)
            {
                await Task.Delay(25);
            }

            Assert.True(bus.SubscribeAttempts >= 2);
            Assert.NotNull(bus.OnRemote);

            var before = h.Validator.Batches.Count;
            bus.OnRemote!(); // what a message from another instance does
            for (var waited = 0; waited < 5000 && h.Validator.Batches.Count == before; waited += 25)
            {
                await Task.Delay(25);
            }

            Assert.True(h.Validator.Batches.Count > before);
            Assert.True(connection.Aborts >= 1);
        }
        finally
        {
            await h.Monitor.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(30, 40, 10)] // maximum unverified age shorter than two intervals
    [InlineData(30, 90, 90)] // validation timeout not shorter than the maximum unverified age
    public void Inconsistent_revalidation_options_are_rejected_at_start(int intervalSeconds, int maxAgeSeconds, int timeoutSeconds)
    {
        var services = new ServiceCollection();
        services.AddRealtime(o =>
        {
            o.RevalidationInterval = TimeSpan.FromSeconds(intervalSeconds);
            o.MaxUnverifiedAge = TimeSpan.FromSeconds(maxAgeSeconds);
            o.ValidationTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RealtimeOptions>>().Value);
    }

    [Fact]
    public void The_default_revalidation_options_are_the_approved_values()
    {
        var options = new RealtimeOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), options.RevalidationInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), options.MaxUnverifiedAge);
    }
}

/// <summary>The real SignalR pipeline: aborting from the monitor's thread (outside any hub invocation) must close the client cleanly and clean up.</summary>
public sealed class RevalidationHubTests : IAsyncLifetime
{
    private readonly ScriptedValidator _validator = new();
    private IHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                s.AddAuthorization();
                s.AddRealtime(o =>
                {
                    o.RevalidationInterval = TimeSpan.FromHours(1); // only the explicit passes below run
                    o.MaxUnverifiedAge = TimeSpan.FromHours(2);
                });
                s.AddSingleton<IRealtimeConnectionValidator>(_validator);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapRealtimeHub());
            })).StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<(HubConnection Connection, Task Closed)> ConnectAsync(string user)
    {
        var server = _host.GetTestServer();
        var connection = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, "/hubs/realtime"), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            o.WebSocketFactory = (ctx, ct) => new ValueTask<System.Net.WebSockets.WebSocket>(
                server.CreateWebSocketClient().ConnectAsync(new Uri($"{ctx.Uri}&access_token={user}"), ct));
            o.AccessTokenProvider = () => Task.FromResult<string?>(user);
        }).Build();
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync();
        return (connection, closed.Task);
    }

    [Fact]
    public async Task The_monitor_closes_only_the_connections_the_validator_rejects_and_cleans_up_after_them()
    {
        var registry = _host.Services.GetRequiredService<RealtimeConnectionRegistry>();
        var monitor = _host.Services.GetRequiredService<RealtimeConnectionMonitor>();
        var (kept, keptClosed) = await ConnectAsync("kept");
        var (revoked, revokedClosed) = await ConnectAsync("revoked");
        Assert.Equal(2, registry.Count);
        _validator.Handler = (connections, _) => Task.FromResult<IReadOnlySet<string>>(
            connections.Where(c => c.User.FindFirst("sub")!.Value == "kept").Select(c => c.ConnectionId).ToHashSet());

        await monitor.RunCycleAsync(CancellationToken.None); // runs on this test thread, not inside a hub invocation

        Assert.Same(revokedClosed, await Task.WhenAny(revokedClosed, Task.Delay(5000)));
        Assert.Equal(HubConnectionState.Disconnected, revoked.State);
        Assert.Equal(HubConnectionState.Connected, kept.State);
        Assert.False(keptClosed.IsCompleted);
        for (var waited = 0; waited < 3000 && registry.Count != 1; waited += 25)
        {
            await Task.Delay(25);
        }

        Assert.Equal(1, registry.Count); // the disconnect callback removed the aborted connection

        await monitor.RunCycleAsync(CancellationToken.None); // aborting what already ended is harmless
        Assert.Equal(HubConnectionState.Connected, kept.State);
        await kept.DisposeAsync();
        await revoked.DisposeAsync();
    }
}
