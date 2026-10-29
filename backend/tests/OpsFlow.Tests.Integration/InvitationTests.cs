using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Invitations;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public class InvitationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Invite(Guid Id, string Email, string Role, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

    private async Task<(ApiClient Owner, OrgDto Org)> NewTenantAsync()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        return (owner, await owner.CreateOrganizationAsync());
    }

    private async Task<Invite> InviteAsync(ApiClient owner, Guid tenantId, string email, string role = "Agent") =>
        await owner.ReadAsync<Invite>(await owner.PostAsync($"/api/v1/tenants/{tenantId}/invitations", new { email, role }));

    [Fact]
    public async Task Invited_user_can_accept_once_and_the_token_is_only_stored_hashed()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner, org.Id, email, "Manager");
        var invitee = await ApiClient.RegisterAsync(factory, email);
        var token = ApiClient.InvitationTokenFrom(factory, email);

        var accepted = await invitee.ReadAsync<OrgDto>(await invitee.PostAsync("/api/v1/invitations/accept", new { token }));

        Assert.Equal(org.Id, accepted.Id);
        Assert.Equal("Manager", accepted.Role);
        Assert.Contains(await owner.MembersAsync(org.Id), m => m.UserId == invitee.UserId && m.Role == "Manager");
        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync("/api/v1/invitations/accept", new { token })).StatusCode); // idempotent retry
        var stranger = await ApiClient.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostAsync("/api/v1/invitations/accept", new { token })).StatusCode); // already used: indistinguishable from invalid

        using var scope = factory.Services.CreateScope();
        var hashes = await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Invitations.IgnoreQueryFilters().Select(i => i.TokenHash).ToListAsync();
        Assert.DoesNotContain(token, hashes);
        Assert.Contains(InvitationToken.Hash(token), hashes);
    }

    [Fact]
    public async Task Invitations_expire_after_72_hours()
    {
        var (owner, org) = await NewTenantAsync();
        await InviteAsync(owner, org.Id, $"ttl-{Guid.NewGuid():N}@example.com");

        var pending = await owner.ReadAsync<List<Invite>>(await owner.GetAsync($"/api/v1/tenants/{org.Id}/invitations"));

        Assert.Equal(TimeSpan.FromHours(72), pending.Single().ExpiresAtUtc - pending.Single().CreatedAtUtc);
    }

    [Fact]
    public async Task Accepting_twice_or_concurrently_is_idempotent_for_the_same_user()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"retry-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner, org.Id, email);
        var invitee = await ApiClient.RegisterAsync(factory, email);
        var token = ApiClient.InvitationTokenFrom(factory, email);

        var racing = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => invitee.PostAsync("/api/v1/invitations/accept", new { token })));
        var retry = await invitee.PostAsync("/api/v1/invitations/accept", new { token });

        Assert.All(racing, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(await owner.MembersAsync(org.Id), m => m.UserId == invitee.UserId);
    }

    [Fact]
    public async Task Database_rejects_an_inviter_from_another_tenant()
    {
        var (_, orgA) = await NewTenantAsync();
        var (ownerB, orgB) = await NewTenantAsync();
        var foreignInviter = (await ownerB.MembersAsync(orgB.Id)).Single();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().Begin(orgA.Id);
        db.Invitations.Add(Invitation.Create(orgA.Id, "x@example.com", TenantRole.Agent, "hash-fk", foreignInviter.Id, DateTime.UtcNow, TimeSpan.FromHours(72)));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Managers_can_only_revoke_agent_invitations()
    {
        var (owner, org) = await NewTenantAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var agentInvite = await InviteAsync(owner, org.Id, $"a-{Guid.NewGuid():N}@example.com", "Agent");
        var managerInvite = await InviteAsync(owner, org.Id, $"m-{Guid.NewGuid():N}@example.com", "Manager");
        var ownerInvite = await InviteAsync(owner, org.Id, $"o-{Guid.NewGuid():N}@example.com", "TenantOwner");
        string Url(Invite i) => $"/api/v1/tenants/{org.Id}/invitations/{i.Id}";

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync(Url(managerInvite))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync(Url(ownerInvite))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(Url(agentInvite))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(managerInvite))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Url(ownerInvite))).StatusCode);
    }

    [Fact]
    public async Task Accepting_as_an_existing_member_keeps_the_role_and_consumes_the_invitation()
    {
        var (owner, org) = await NewTenantAsync();
        var agent = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Agent");
        await InviteAsync(owner, org.Id, agent.Email, "Manager");
        var token = ApiClient.InvitationTokenFrom(factory, agent.Email);

        var accepted = await agent.ReadAsync<OrgDto>(await agent.PostAsync("/api/v1/invitations/accept", new { token }));

        Assert.Equal("Agent", accepted.Role);
        Assert.Equal("Agent", (await owner.MembersAsync(org.Id)).Single(m => m.UserId == agent.UserId).Role);
        Assert.Empty(await owner.ReadAsync<List<Invite>>(await owner.GetAsync($"/api/v1/tenants/{org.Id}/invitations")));
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.PostAsync("/api/v1/invitations/accept", new { token })).StatusCode);
    }

    [Fact]
    public async Task Another_users_account_cannot_accept_the_invitation()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"target-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner, org.Id, email);
        var token = ApiClient.InvitationTokenFrom(factory, email);
        var stranger = await ApiClient.RegisterAsync(factory);

        var response = await stranger.PostAsync("/api/v1/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(await owner.MembersAsync(org.Id), m => m.UserId == stranger.UserId);
    }

    [Fact]
    public async Task Garbage_token_is_rejected()
    {
        var user = await ApiClient.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/v1/invitations/accept", new { token = "not-a-real-token" })).StatusCode);
    }

    [Fact]
    public async Task Revoked_invitation_cannot_be_accepted()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"revoked-{Guid.NewGuid():N}@example.com";
        var invite = await InviteAsync(owner, org.Id, email);
        var invitee = await ApiClient.RegisterAsync(factory, email);
        var token = ApiClient.InvitationTokenFrom(factory, email);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/tenants/{org.Id}/invitations/{invite.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await invitee.PostAsync("/api/v1/invitations/accept", new { token })).StatusCode);
    }

    [Fact]
    public async Task Expired_invitation_cannot_be_accepted_and_can_be_reissued()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"expired-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner, org.Id, email);
        var invitee = await ApiClient.RegisterAsync(factory, email);
        var token = ApiClient.InvitationTokenFrom(factory, email);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Invitations.IgnoreQueryFilters()
                .Where(i => i.TenantId == org.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAtUtc, DateTime.UtcNow.AddDays(-1)));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await invitee.PostAsync("/api/v1/invitations/accept", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email, role = "Agent" })).StatusCode);
    }

    [Fact]
    public async Task Duplicate_pending_invitation_is_a_conflict()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner, org.Id, email);

        var second = await owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email, role = "Agent" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Invitation_cannot_target_the_customer_role_and_listing_hides_tokens()
    {
        var (owner, org) = await NewTenantAsync();

        var customer = await owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "c@example.com", role = "Customer" });
        await InviteAsync(owner, org.Id, $"listed-{Guid.NewGuid():N}@example.com");
        var list = await owner.GetAsync($"/api/v1/tenants/{org.Id}/invitations");

        Assert.Equal(HttpStatusCode.BadRequest, customer.StatusCode);
        Assert.DoesNotContain("token", (await list.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_email_returns_validation_problem()
    {
        var (owner, org) = await NewTenantAsync();

        var response = await owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "not-an-email", role = "Agent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("errors", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
