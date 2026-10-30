using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>SignalR authentication, authorization-aware delivery and reconnect recovery against the real API pipeline.</summary>
public class RealtimeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HubClient> ConnectAsync(ApiClient who)
    {
        var hub = HubClient.Create(factory, who.AccessToken);
        await hub.StartAsync();
        return hub;
    }

    [Fact]
    public async Task Anonymous_invalid_and_refresh_tokens_cannot_connect()
    {
        var user = await ApiClient.RegisterAsync(factory);

        await using var anonymous = HubClient.Create(factory, null);
        await Assert.ThrowsAnyAsync<Exception>(anonymous.StartAsync);

        await using var garbage = HubClient.Create(factory, "not.a.jwt");
        await Assert.ThrowsAnyAsync<Exception>(garbage.StartAsync);

        await using var refresh = HubClient.Create(factory, user.RefreshToken); // a signed JWT, but token_type=refresh
        await Assert.ThrowsAnyAsync<Exception>(refresh.StartAsync);

        await using var ok = HubClient.Create(factory, user.AccessToken);
        await ok.StartAsync();
        Assert.Equal(HubConnectionState.Connected, ok.Connection.State);
    }

    [Fact]
    public async Task A_revoked_session_cannot_connect()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var token = user.AccessToken;
        (await user.PostAsync("/api/v1/auth/logout", new { refreshToken = user.RefreshToken })).EnsureSuccessStatusCode();

        await using var hub = HubClient.Create(factory, token);
        await Assert.ThrowsAnyAsync<Exception>(hub.StartAsync);
    }

    [Fact]
    public async Task Clients_cannot_grant_themselves_group_access()
    {
        var user = await ApiClient.RegisterAsync(factory);
        await using var hub = await ConnectAsync(user);

        Assert.False(await hub.Connection.InvokeAsync<bool>("JoinGroup", "tenant:anything"));
        Assert.False(await hub.Connection.InvokeAsync<bool>("JoinGroup", new string('g', 500)));
    }

    [Fact]
    public async Task Messages_are_pushed_only_to_the_people_who_may_see_them_with_the_right_view()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership);
        await using var customer = await ConnectAsync(w.Customer);
        await using var agent1 = await ConnectAsync(w.Agent1);
        await using var agent2 = await ConnectAsync(w.Agent2); // staff of the tenant, but not assigned
        await using var manager = await ConnectAsync(w.Manager);

        var sent = await w.Agent1.ReadAsync<MessageJson>(await w.SendMessageAsync(w.Agent1, ticket.Id, "live hello"));
        await w.AddNoteAsync(w.Agent1, ticket.Id, "private live note");

        bool Msg((string Name, JsonElement Payload) e) => e.Name == "ticket.message.created" && e.Payload.GetProperty("message").GetProperty("content").GetString() == "live hello";
        bool Note((string Name, JsonElement Payload) e) => e.Name == "ticket.note.created";
        Assert.True(await customer.WaitForAsync(Msg));
        Assert.True(await agent1.WaitForAsync(Msg));
        Assert.True(await manager.WaitForAsync(Msg));
        Assert.True(await manager.WaitForAsync(Note));
        Assert.True(await agent1.WaitForAsync(Note));

        // the customer view of the event hides the staff identity; the staff view keeps it
        var customerEvent = customer.Events.Single(e => Msg(e)).Payload.GetProperty("message");
        Assert.Equal(JsonValueKind.Null, customerEvent.GetProperty("senderUserId").ValueKind);
        Assert.Equal(w.Agent1.UserId, manager.Events.Single(e => Msg(e)).Payload.GetProperty("message").GetProperty("senderUserId").GetGuid());
        Assert.Equal(w.Org.Id, customer.Events.Single(e => Msg(e)).Payload.GetProperty("tenantId").GetGuid());

        // negatives (a positive control above proves delivery had already happened)
        Assert.DoesNotContain(customer.Events, Note);
        Assert.DoesNotContain(customer.Events, e => e.Payload.ToString().Contains("private live note", StringComparison.Ordinal));
        Assert.DoesNotContain(agent2.Events, e => e.Name.StartsWith("ticket.", StringComparison.Ordinal));

        // persistence precedes publishing: whatever was pushed is already readable through REST
        Assert.Contains(await w.MessagesAsync(w.Customer, ticket.Id), m => m.Id == sent.Id);
    }

    [Fact]
    public async Task Notifications_are_pushed_live_to_their_owner_only_and_recovered_after_a_reconnect()
    {
        var w = await TicketWorld.CreateAsync(factory);
        await using var manager = await ConnectAsync(w.Manager);
        await using var customer = await ConnectAsync(w.Customer);

        var ticket = await w.CreateTicketAsync(w.Customer);

        Assert.True(await manager.WaitForAsync(e => e.Name == "notification.created" && e.Payload.GetProperty("notification").GetProperty("type").GetString() == "TicketCreated"));
        Assert.DoesNotContain(customer.Events, e => e.Name == "notification.created");

        // missed events: the customer is offline while staff replies, then catches up from the server history
        await customer.DisposeAsync();
        await w.AssignAsync(ticket.Id, w.Agent1Membership);
        await w.SendMessageAsync(w.Agent1, ticket.Id, "while you were away");
        await using var back = await ConnectAsync(w.Customer);
        Assert.Contains(await w.NotificationsAsync(w.Customer), n => n.Type == "MessageReceived");
        Assert.Contains(await w.MessagesAsync(w.Customer, ticket.Id, "?afterSequence=0"), m => m.Content == "while you were away");
        Assert.Empty(back.Events); // nothing is replayed over the socket: the history endpoints are the recovery path
    }

    [Fact]
    public async Task Suspended_and_unassigned_staff_stop_receiving_private_events()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.AssignAsync((await w.CreateTicketAsync(w.Customer)).Id, w.Agent1Membership, w.Agent2Membership);
        await using var agent1 = await ConnectAsync(w.Agent1);
        await using var agent2 = await ConnectAsync(w.Agent2);

        await w.SendMessageAsync(w.Manager, ticket.Id, "first");
        Assert.True(await agent1.WaitForAsync(e => e.Name == "ticket.message.created"));
        Assert.True(await agent2.WaitForAsync(e => e.Name == "ticket.message.created"));

        (await w.Manager.DeleteAsync(w.Url($"tickets/{ticket.Id}/assignments/{w.Agent1Membership}"))).EnsureSuccessStatusCode();
        (await w.Owner.PutAsync(w.Url($"members/{w.Agent2Membership}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        var before1 = agent1.Events.Count(e => e.Name == "ticket.message.created");
        var before2 = agent2.Events.Count(e => e.Name == "ticket.message.created");

        await w.SendMessageAsync(w.Manager, ticket.Id, "second");
        await w.AddNoteAsync(w.Manager, ticket.Id, "second note");
        await using var control = await ConnectAsync(w.Owner);
        await w.SendMessageAsync(w.Manager, ticket.Id, "third");
        Assert.True(await control.WaitForAsync(e => e.Name == "ticket.message.created"));
        await Task.Delay(300);

        Assert.Equal(before1, agent1.Events.Count(e => e.Name == "ticket.message.created")); // unassigned
        Assert.Equal(before2, agent2.Events.Count(e => e.Name == "ticket.message.created")); // suspended
        Assert.DoesNotContain(agent1.Events, e => e.Name == "ticket.note.created");
        Assert.DoesNotContain(agent2.Events, e => e.Name == "ticket.note.created");
    }

    [Fact]
    public async Task Hub_connections_are_not_shared_across_tenants()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        await using var otherOwner = await ConnectAsync(b.Owner);
        await using var control = await ConnectAsync(a.Owner);

        var ticket = await a.CreateTicketAsync(a.Customer);
        await a.SendMessageAsync(a.Customer, ticket.Id, "tenant a only");

        Assert.True(await control.WaitForAsync(e => e.Name == "ticket.message.created"));
        await Task.Delay(300);
        Assert.Empty(otherOwner.Events);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url($"tickets/{ticket.Id}/messages"))).StatusCode);
    }
}
