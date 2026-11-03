using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// A revoked session must lose its already-open realtime connection, and only that one. Here only the post-commit fast path can close a
/// connection (the periodic check is set to 30 minutes), so every close below is proof that the revocation itself triggered it. The healthy
/// bound is one revalidation query; the 8 s wait in the assertions only leaves room for a slow CI machine.
/// </summary>
public class RealtimeRevocationTests(FastPathOnlyApiFactory factory) : IClassFixture<FastPathOnlyApiFactory>
{
    private const string NewPassword = "N3w!Passw0rd#2";

    private async Task<HubClient> ConnectAsync(ApiClient who)
    {
        var hub = HubClient.Create(factory, who.AccessToken);
        await hub.StartAsync();
        return hub;
    }

    /// <summary>A second device of the same user: its own login, so its own session.</summary>
    private async Task<ApiClient> SecondDeviceAsync(ApiClient first)
    {
        var device = factory.CreateApiClient();
        (await device.LoginAsync(first.Email, ApiClient.Password)).EnsureSuccessStatusCode();
        return device;
    }

    [Fact]
    public async Task Chrome_revokes_Edge_and_the_open_Edge_connection_is_closed_while_Chrome_keeps_working()
    {
        var chrome = await ApiClient.RegisterAsync(factory);
        var edge = await SecondDeviceAsync(chrome);
        await using var chromeHub = await ConnectAsync(chrome);
        await using var edgeHub = await ConnectAsync(edge);

        var sessions = await chrome.ReadAsync<List<SessionDto>>(await chrome.GetAsync("/api/v1/auth/sessions"));
        var edgeSession = sessions.Single(s => !s.IsCurrent);
        Assert.Equal(HttpStatusCode.NoContent, (await chrome.DeleteAsync($"/api/v1/auth/sessions/{edgeSession.Id}")).StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(edgeHub));
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.GetAsync("/api/v1/auth/me")).StatusCode); // its still-unexpired access token is rejected
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.RefreshAsync()).StatusCode); // and it cannot refresh

        Assert.Equal(HttpStatusCode.OK, (await chrome.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HubConnectionState.Connected, chromeHub.Connection.State);
        await RevocationAsserts.PublishAsync(factory, chrome.UserId, "after-revoke");
        Assert.True(await chromeHub.WaitForAsync(e => e.Name == "test.revocation-ping"));
        Assert.DoesNotContain(edgeHub.Events, e => e.Name == "test.revocation-ping"); // the revoked device saw nothing published after the revoke
    }

    [Fact]
    public async Task Logging_out_closes_only_that_sessions_connection()
    {
        var first = await ApiClient.RegisterAsync(factory);
        var second = await SecondDeviceAsync(first);
        await using var firstHub = await ConnectAsync(first);
        await using var secondHub = await ConnectAsync(second);

        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/api/v1/auth/logout", new { refreshToken = second.RefreshToken })).StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(secondHub));
        Assert.True(await RevocationAsserts.StaysConnectedAsync(firstHub));
    }

    [Fact]
    public async Task Logout_all_closes_every_connection_of_the_user_and_leaves_other_users_and_tenants_alone()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var otherDevice = await SecondDeviceAsync(user);
        var neighbour = await ApiClient.RegisterAsync(factory);
        await user.CreateOrganizationAsync("Org One");
        await neighbour.CreateOrganizationAsync("Org Two");
        await using var hubA = await ConnectAsync(user);
        await using var hubB = await ConnectAsync(otherDevice);
        await using var neighbourHub = await ConnectAsync(neighbour);

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/v1/auth/logout-all")).StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(hubA));
        Assert.True(await RevocationAsserts.ClosedAsync(hubB));
        Assert.True(await RevocationAsserts.StaysConnectedAsync(neighbourHub));
        await RevocationAsserts.PublishAsync(factory, neighbour.UserId, "still-here");
        Assert.True(await neighbourHub.WaitForAsync(e => e.Name == "test.revocation-ping"));
    }

    [Fact]
    public async Task Changing_the_password_closes_the_old_connections_and_the_new_session_can_connect()
    {
        var chrome = await ApiClient.RegisterAsync(factory);
        var edge = await SecondDeviceAsync(chrome);
        await using var chromeHub = await ConnectAsync(chrome);
        await using var edgeHub = await ConnectAsync(edge);

        Assert.True((await chrome.ChangePasswordAsync(ApiClient.Password, NewPassword)).IsSuccessStatusCode); // revokes every session, starts a new one for Chrome

        Assert.True(await RevocationAsserts.ClosedAsync(chromeHub)); // the old token's session is gone, even on the device that changed it
        Assert.True(await RevocationAsserts.ClosedAsync(edgeHub));
        await using var fresh = await ConnectAsync(chrome); // the new session's token connects and is left alone
        Assert.True(await RevocationAsserts.StaysConnectedAsync(fresh));
    }

    [Fact]
    public async Task Resetting_the_password_closes_every_connection()
    {
        var user = await ApiClient.RegisterAsync(factory);
        await using var hub = await ConnectAsync(user);
        var anonymous = factory.CreateApiClient();
        (await anonymous.PostAsync("/api/v1/auth/forgot-password", new { email = user.Email })).EnsureSuccessStatusCode();
        var token = ApiClient.ParseLink(factory.Emails.Last(user.Email, "/auth/reset-password").Body).Token;

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword = NewPassword })).StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(hub));
    }

    [Fact]
    public async Task Deactivating_the_account_closes_its_connections()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var bystander = await ApiClient.RegisterAsync(factory);
        await using var hub = await ConnectAsync(user);
        await using var bystanderHub = await ConnectAsync(bystander);

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(hub));
        Assert.True(await RevocationAsserts.StaysConnectedAsync(bystanderHub));
    }

    [Fact]
    public async Task Replaying_a_used_refresh_token_revokes_the_session_and_closes_its_connection()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var raw = new RawAuth(factory);
        var (login, refreshToken, accessToken) = await raw.LoginAsync(user.Email, ApiClient.Password);
        Assert.True(login.IsSuccessStatusCode);
        await using var hub = HubClient.Create(factory, accessToken);
        await hub.StartAsync();

        Assert.True((await raw.RefreshAsync(refreshToken!)).Response.IsSuccessStatusCode); // rotation: the first token is now used
        factory.Clock.Advance(TimeSpan.FromSeconds(30)); // beyond the replay grace window: the second use is theft
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(refreshToken!)).Response.StatusCode);

        Assert.True(await RevocationAsserts.ClosedAsync(hub));
    }

    [Fact]
    public async Task Suspension_in_one_tenant_stops_that_tenants_events_but_leaves_a_valid_session_connected_for_the_other_tenant()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var shared = b.Agent1; // an Agent of tenant B ...
        (await a.Owner.PostAsync(a.Url("invitations"), new { email = shared.Email, role = "Agent" })).EnsureSuccessStatusCode();
        (await shared.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, shared.Email) })).EnsureSuccessStatusCode();
        var membershipInA = (await a.Owner.MembersAsync(a.Org.Id)).Single(m => m.UserId == shared.UserId).Id; // ... and of tenant A
        var ticketA = await a.AssignAsync((await a.CreateTicketAsync(a.Customer)).Id, membershipInA);
        var ticketB = await b.AssignAsync((await b.CreateTicketAsync(b.Customer)).Id, b.Agent1Membership);
        await using var hub = await ConnectAsync(shared);
        int Delivered() => hub.Events.Count(e => e.Name == "ticket.message.created");

        await a.SendMessageAsync(a.Manager, ticketA.Id, "a-1");
        Assert.True(await hub.WaitForAsync(e => e.Name == "ticket.message.created"));
        var before = Delivered();

        (await a.Owner.PutAsync(a.Url($"members/{membershipInA}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        await factory.Services.GetRequiredService<OpsFlow.Realtime.IRealtimeConnectionRevalidation>().RequestAsync();
        await a.SendMessageAsync(a.Manager, ticketA.Id, "a-2");
        await factory.DrainOutboxAsync();
        await Task.Delay(500);

        Assert.Equal(before, Delivered()); // suspended in A: nothing new from A reaches the open connection
        Assert.Equal(HubConnectionState.Connected, hub.Connection.State); // the session is still valid: revalidation does not over-close

        await b.SendMessageAsync(b.Manager, ticketB.Id, "b-1");
        for (var waited = 0; waited < 5000 && Delivered() == before; waited += 50)
        {
            await factory.DrainOutboxAsync();
            await Task.Delay(50);
        }

        Assert.True(Delivered() > before); // tenant B is unaffected
    }
}

/// <summary>The periodic check and the fail-closed rule, observable in seconds (interval 200 ms, maximum unverified age 2 s).</summary>
public class RealtimePeriodicRevalidationTests(PeriodicRevalidationApiFactory factory) : IClassFixture<PeriodicRevalidationApiFactory>
{
    private async Task<HubClient> ConnectAsync(ApiClient who)
    {
        var hub = HubClient.Create(factory, who.AccessToken);
        await hub.StartAsync();
        return hub;
    }

    [Fact]
    public async Task The_periodic_check_closes_a_revoked_connection_without_any_trigger_and_spares_the_valid_one()
    {
        var first = await ApiClient.RegisterAsync(factory);
        var second = factory.CreateApiClient();
        (await second.LoginAsync(first.Email, ApiClient.Password)).EnsureSuccessStatusCode();
        await using var firstHub = await ConnectAsync(first);
        await using var secondHub = await ConnectAsync(second);
        var secondSession = (await first.ReadAsync<List<SessionDto>>(await first.GetAsync("/api/v1/auth/sessions"))).Single(s => !s.IsCurrent);

        // Revoked straight in the database: no endpoint, no post-commit request. Only the timer can notice.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
            var session = await db.UserSessions.SingleAsync(s => s.Id == secondSession.Id);
            session.Revoke("test", factory.Clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync();
        }

        Assert.True(await RevocationAsserts.ClosedAsync(secondHub));
        Assert.True(await RevocationAsserts.StaysConnectedAsync(firstHub));
    }

    [Fact]
    public async Task A_valid_connection_survives_many_checks_and_one_temporary_validation_failure()
    {
        var user = await ApiClient.RegisterAsync(factory);
        await using var hub = await ConnectAsync(user);
        Assert.True(await RevocationAsserts.StaysConnectedAsync(hub, 1500)); // several healthy cycles

        factory.Validation.Failing = true;
        try
        {
            await Task.Delay(600); // a few failed cycles, well below the 2 s maximum unverified age
        }
        finally
        {
            factory.Validation.Failing = false;
        }

        Assert.True(await RevocationAsserts.StaysConnectedAsync(hub, 1500)); // verified again, never closed
    }

    [Fact]
    public async Task Repeated_validation_failures_close_the_connection_once_the_maximum_unverified_age_is_exceeded()
    {
        var user = await ApiClient.RegisterAsync(factory);
        await using var hub = await ConnectAsync(user);

        factory.Validation.Failing = true;
        try
        {
            Assert.True(await RevocationAsserts.StaysConnectedAsync(hub, 800)); // one or a few failures are tolerated
            Assert.True(await RevocationAsserts.ClosedAsync(hub)); // an outage must not keep it open indefinitely
        }
        finally
        {
            factory.Validation.Failing = false;
        }
    }
}
