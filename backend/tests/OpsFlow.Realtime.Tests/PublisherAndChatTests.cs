using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpsFlow.Realtime.Chat;

namespace OpsFlow.Realtime.Tests;

/// <summary>Records every send; can simulate a transport failure.</summary>
internal sealed class FakeProxy : IClientProxy
{
    public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];

    public Func<Exception?> Fail { get; set; } = () => null;

    public string Target { get; set; } = string.Empty;

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail() is { } error)
        {
            throw error;
        }

        Sent.Add((Target, method, args));
        return Task.CompletedTask;
    }
}

internal sealed class FakeClients(FakeProxy proxy) : IHubClients
{
    private FakeProxy For(string target)
    {
        proxy.Target = target;
        return proxy;
    }

    public IClientProxy All => For("all");

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => For("all-except");

    public IClientProxy Client(string connectionId) => For("client:" + connectionId);

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => For("clients");

    public IClientProxy Group(string groupName) => For("group:" + groupName);

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => For("group-except");

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => For("groups");

    public IClientProxy User(string userId) => For("user:" + userId);

    public IClientProxy Users(IReadOnlyList<string> userIds) => For("users:" + string.Join(',', userIds.Order()));
}

internal sealed class FakeHubContext : IHubContext<RealtimeHub>
{
    public FakeHubContext() => Clients = new FakeClients(Proxy);

    public FakeProxy Proxy { get; } = new();

    public IHubClients Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException();
}

public sealed class PublisherAndChatTests
{
    private sealed class Members(params string[] users) : IRealtimeGroupMembership
    {
        public ValueTask<IReadOnlyCollection<string>> GetMemberUserIdsAsync(string group, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyCollection<string>>(users);
    }

    private static (ServiceProvider Provider, FakeHubContext Hub) Build(Action<IServiceCollection>? extra = null)
    {
        var hub = new FakeHubContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRealtime();
        services.AddSingleton<IHubContext<RealtimeHub>>(hub);
        extra?.Invoke(services);
        return (services.BuildServiceProvider(), hub);
    }

    // ---------------------------------------------------------------- publisher

    [Fact]
    public async Task Invalid_configuration_throws_and_sends_nothing()
    {
        var (provider, hub) = Build();
        var publisher = provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimePublisher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.NotifyUserAsync("alice", new UnnamedEvent("x"))); // no [RealtimeEvent]
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.NotifyUsersAsync([], new UnnamedEvent("x"))); // still validated when nobody is targeted
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.NotifyUserAsync(" ", new Ping("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.NotifyUsersAsync(["alice", ""], new Ping("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishGroupAsync("", new Ping("x")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.SendToUsersAsync(["alice"], null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishToGroupMembersAsync("room", new Ping("x"))); // no IRealtimeGroupMembership registered
        Assert.Empty(hub.Proxy.Sent);
    }

    [Fact]
    public async Task Events_are_sent_through_the_single_receive_callback_with_their_declared_name()
    {
        var (provider, hub) = Build();
        var publisher = provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimePublisher>();

        await publisher.NotifyUsersAsync(["bob", "alice"], new Ping("hi"));

        var sent = Assert.Single(hub.Proxy.Sent);
        Assert.Equal(("users:alice,bob", "Receive"), (sent.Target, sent.Method));
        Assert.Equal("test.ping", sent.Args[0]);
        Assert.Equal(new Ping("hi"), sent.Args[1]);
    }

    [Fact]
    public async Task Transport_failures_are_swallowed_after_commit_but_cancellation_is_honoured()
    {
        var (provider, hub) = Build();
        var publisher = provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimePublisher>();
        hub.Proxy.Fail = () => new IOException("socket closed");

        await publisher.NotifyUserAsync("alice", new Ping("x")); // best effort: no exception
        hub.Proxy.Fail = () => new OperationCanceledException(); // a transport-level cancellation is still only a failed delivery
        await publisher.NotifyUserAsync("alice", new Ping("x"));

        hub.Proxy.Fail = () => null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.NotifyUserAsync("alice", new Ping("x"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.NotifyUsersAsync(["alice"], new Ping("x"), cancelled.Token));
        Assert.Empty(hub.Proxy.Sent);
    }

    [Fact]
    public async Task Private_group_publishing_resolves_the_current_members_at_publish_time()
    {
        var (provider, hub) = Build(s => s.AddSingleton<IRealtimeGroupMembership>(new Members("alice", "carol")));
        var publisher = provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimePublisher>();

        await publisher.PublishToGroupMembersAsync("room-1", new Ping("secret"));

        Assert.Equal("users:alice,carol", Assert.Single(hub.Proxy.Sent).Target); // a user-targeted send, never the SignalR group
    }

    // ---------------------------------------------------------------- chat

    private sealed class Store : IChatMessageStore<string>
    {
        public List<ChatSendRequest> Requests { get; } = [];

        public List<string> Log { get; } = [];

        public object? SecondEvent { get; init; }

        public Task<ChatAppendResult<string>> AppendAsync(ChatSendRequest request, CancellationToken cancellationToken)
        {
            Log.Add("append");
            var duplicate = Requests.Any(r => r.ClientMessageId == request.ClientMessageId);
            Requests.Add(request);
            if (duplicate)
            {
                return Task.FromResult(new ChatAppendResult<string>("dup", true, []));
            }

            // recipient-specific views: the host decides who gets which payload
            List<ChatDelivery> deliveries = [new(["staff-1", "staff-2"], new Ping("staff view: " + request.Content)), new(["customer-1"], SecondEvent ?? new Ping("customer view"))];
            return Task.FromResult(new ChatAppendResult<string>("stored", false, deliveries));
        }
    }

    private sealed class Rooms : IChatConversationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(ChatUser user, string conversationId, ChatOperation operation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(user.Id == "alice" && conversationId == "room-1");
    }

    private static ChatService<string> Chat(Store store, IServiceProvider provider) =>
        new(new Rooms(), store, new DefaultChatMessageValidator(10), provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimePublisher>(), provider.GetRequiredService<ILogger<ChatService<string>>>());

    private static ClaimsPrincipal Authenticated(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public async Task The_sender_comes_only_from_the_authenticated_identity()
    {
        var (provider, _) = Build();
        var store = new Store();
        var chat = Chat(store, provider);

        // a spoofed "userId"/"sender" claim or header-like value is irrelevant: only the configured claim of an AUTHENTICATED principal counts
        var alice = ChatUser.FromPrincipal(Authenticated(new Claim("sub", "alice"), new Claim("userId", "admin"), new Claim("sender", "admin")));
        await chat.SendAsync(alice, "room-1", "c1", "hello");

        Assert.Equal("alice", Assert.Single(store.Requests).Sender.Id);
        Assert.Equal(ChatFailure.Unauthenticated, Assert.Throws<ChatException>(() => ChatUser.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity()))).Failure); // not authenticated
        Assert.Equal(ChatFailure.Unauthenticated, Assert.Throws<ChatException>(() => ChatUser.FromPrincipal(Authenticated(new Claim("name", "x")))).Failure); // no id claim
        Assert.Equal(ChatFailure.Unauthenticated, Assert.Throws<ChatException>(() => ChatUser.FromAuthenticatedHostContext(" ")).Failure);
        Assert.Equal("u1", ChatUser.FromPrincipal(Authenticated(new Claim("uid", "u1")), "uid").Id);

        // the authorizer judges the authenticated user, not any body value
        await Assert.ThrowsAsync<ChatException>(() => chat.SendAsync(ChatUser.FromPrincipal(Authenticated(new Claim("sub", "mallory"))), "room-1", "c2", "hi"));
        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task Chat_validates_authorizes_persists_before_publishing_and_publishes_each_audience_its_own_view()
    {
        var (provider, hub) = Build();
        var store = new Store();
        var chat = Chat(store, provider);
        var alice = ChatUser.FromAuthenticatedHostContext("alice");

        var first = await chat.SendAsync(alice, "room-1", "c1", "hello");
        var retry = await chat.SendAsync(alice, "room-1", "c1", "hello");

        Assert.False(first.Duplicate);
        Assert.True(retry.Duplicate);
        Assert.Equal(2, hub.Proxy.Sent.Count); // staff view + customer view; the duplicate published nothing
        Assert.Equal("users:staff-1,staff-2", hub.Proxy.Sent[0].Target);
        Assert.Equal(new Ping("staff view: hello"), hub.Proxy.Sent[0].Args[1]);
        Assert.Equal("users:customer-1", hub.Proxy.Sent[1].Target);
        Assert.Equal(new Ping("customer view"), hub.Proxy.Sent[1].Args[1]);

        Assert.Equal(ChatFailure.Invalid, (await Assert.ThrowsAsync<ChatException>(() => chat.SendAsync(alice, "room-1", "c2", "  "))).Failure);
        Assert.Equal(ChatFailure.Invalid, (await Assert.ThrowsAsync<ChatException>(() => chat.SendAsync(alice, "room-1", "c3", new string('x', 11)))).Failure);
        Assert.Equal(ChatFailure.Invalid, (await Assert.ThrowsAsync<ChatException>(() => chat.SendAsync(alice, "room-1", "", "hi"))).Failure);
        Assert.Equal(ChatFailure.Forbidden, (await Assert.ThrowsAsync<ChatException>(() => chat.SendAsync(alice, "room-2", "c5", "hi"))).Failure);
        Assert.Equal(["append", "append"], store.Log);
    }

    [Fact]
    public async Task A_failed_or_misconfigured_push_never_undoes_or_hides_the_committed_message()
    {
        var (provider, hub) = Build();
        var chat = Chat(new Store { SecondEvent = new UnnamedEvent("no event name") }, provider);
        var alice = ChatUser.FromAuthenticatedHostContext("alice");

        var misconfigured = await chat.SendAsync(alice, "room-1", "c1", "hello"); // second delivery has an invalid event: logged, message still returned
        Assert.Equal("stored", misconfigured.Message);
        Assert.Single(hub.Proxy.Sent); // the valid delivery still went out

        hub.Proxy.Fail = () => new IOException("socket closed");
        var transport = await chat.SendAsync(alice, "room-1", "c2", "again");
        Assert.Equal("stored", transport.Message);
    }

    private sealed class History : IChatHistoryReader<string>
    {
        public Task<IReadOnlyList<string>> GetHistoryAsync(ChatUser user, string conversationId, long? beforeSequence, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["m" + limit]);
    }

    [Fact]
    public async Task History_requires_read_authorization_and_clamps_the_limit()
    {
        var history = new ChatHistoryService<string>(new Rooms(), new History());

        Assert.Equal(["m100"], await history.GetHistoryAsync(ChatUser.FromAuthenticatedHostContext("alice"), "room-1", null, 5000));
        await Assert.ThrowsAsync<ChatException>(() => history.GetHistoryAsync(ChatUser.FromAuthenticatedHostContext("mallory"), "room-1", null, 10));
    }
}
