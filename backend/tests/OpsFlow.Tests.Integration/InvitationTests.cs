using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Memberships;
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
        Assert.Contains(SecureToken.Hash(token), hashes);
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

    // ---- invitation authorization at the commit boundary + inviter authority ----

    private (InvitationService Service, OpsFlowDbContext Db, IServiceScope Scope) InviteAs(Guid tenantId, Guid actorMembershipId, TenantRole snapshotRole)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, actorMembershipId, snapshotRole);
        return (scope.ServiceProvider.GetRequiredService<InvitationService>(), scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope);
    }

    private MembershipService MembershipAs(Guid tenantId, Guid actorMembershipId, out IServiceScope scope)
    {
        scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, actorMembershipId, TenantRole.TenantOwner);
        return scope.ServiceProvider.GetRequiredService<MembershipService>();
    }

    private async Task<int> InvitationRowsAsync(string email)
    {
        var normalizedEmail = OpsFlow.Domain.Invitations.Invitation.Normalize(email);
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Invitations.IgnoreQueryFilters().CountAsync(i => i.NormalizedEmail == normalizedEmail);
    }

    [Fact]
    public async Task A_manager_demoted_mid_request_cannot_commit_an_invitation()
    {
        var (owner, org) = await NewTenantAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var members = await owner.MembersAsync(org.Id);
        var ownerMembership = members.Single(m => m.UserId == owner.UserId).Id;
        var managerMembership = members.Single(m => m.UserId == manager.UserId).Id;
        var email = $"stale-{Guid.NewGuid():N}@example.com";
        var (inviter, inviterDb, inviterScope) = InviteAs(org.Id, managerMembership, TenantRole.Manager); // request-start snapshot: Manager
        var demoter = MembershipAs(org.Id, ownerMembership, out var demoterScope);
        using (inviterScope)
        using (demoterScope)
        {
            factory.SaveGate.PauseNextSave(inviterDb);
            var inviting = Task.Run(() => inviter.CreateAsync(email, TenantRole.Agent, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await demoter.ChangeRoleAsync(managerMembership, TenantRole.Agent, CancellationToken.None);
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => inviting.WaitAsync(QueryGate.Timeout))).StatusCode);
            Assert.Equal(0, await InvitationRowsAsync(email));
        }
    }

    [Fact]
    public async Task An_owner_demoted_mid_request_cannot_commit_an_owner_invitation()
    {
        var owner1 = await ApiClient.RegisterAsync(factory);
        var org = await owner1.CreateOrganizationAsync();
        var owner2 = await ApiClient.AddMemberAsync(factory, owner1, org.Id, "TenantOwner");
        var members = await owner1.MembersAsync(org.Id);
        var m1 = members.Single(m => m.UserId == owner1.UserId).Id;
        var m2 = members.Single(m => m.UserId == owner2.UserId).Id;
        var email = $"owner-stale-{Guid.NewGuid():N}@example.com";
        var (inviter, inviterDb, inviterScope) = InviteAs(org.Id, m2, TenantRole.TenantOwner);
        var demoter = MembershipAs(org.Id, m1, out var demoterScope);
        using (inviterScope)
        using (demoterScope)
        {
            factory.SaveGate.PauseNextSave(inviterDb);
            var inviting = Task.Run(() => inviter.CreateAsync(email, TenantRole.TenantOwner, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await demoter.ChangeRoleAsync(m2, TenantRole.Manager, CancellationToken.None); // now only allowed to invite Agents
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => inviting.WaitAsync(QueryGate.Timeout))).StatusCode);
            Assert.Equal(0, await InvitationRowsAsync(email));
        }
    }

    [Fact]
    public async Task A_manager_suspended_mid_request_cannot_commit_a_revocation()
    {
        var (owner, org) = await NewTenantAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var members = await owner.MembersAsync(org.Id);
        var ownerMembership = members.Single(m => m.UserId == owner.UserId).Id;
        var managerMembership = members.Single(m => m.UserId == manager.UserId).Id;
        var invite = await InviteAsync(owner, org.Id, $"revoke-{Guid.NewGuid():N}@example.com");
        var (revoker, revokerDb, revokerScope) = InviteAs(org.Id, managerMembership, TenantRole.Manager);
        var suspender = MembershipAs(org.Id, ownerMembership, out var suspenderScope);
        using (revokerScope)
        using (suspenderScope)
        {
            factory.SaveGate.PauseNextSave(revokerDb);
            var revoking = Task.Run(() => revoker.RevokeAsync(invite.Id, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await suspender.SetActiveAsync(managerMembership, false, CancellationToken.None);
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => revoking.WaitAsync(QueryGate.Timeout))).StatusCode);
            Assert.Single(await owner.ReadAsync<List<Invite>>(await owner.GetAsync($"/api/v1/tenants/{org.Id}/invitations"))); // still pending
        }
    }

    [Fact]
    public async Task An_invitation_stops_working_when_its_inviter_loses_the_required_authority()
    {
        var owner1 = await ApiClient.RegisterAsync(factory);
        var org = await owner1.CreateOrganizationAsync();
        var owner2 = await ApiClient.AddMemberAsync(factory, owner1, org.Id, "TenantOwner");
        var manager = await ApiClient.AddMemberAsync(factory, owner1, org.Id, "Manager");
        var members = await owner1.MembersAsync(org.Id);
        var ownerInviteeEmail = $"later-owner-{Guid.NewGuid():N}@example.com";
        var agentInviteeEmail = $"later-agent-{Guid.NewGuid():N}@example.com";
        await InviteAsync(owner2, org.Id, ownerInviteeEmail, "TenantOwner");
        await InviteAsync(manager, org.Id, agentInviteeEmail, "Agent");
        var ownerInvitee = await ApiClient.RegisterAsync(factory, ownerInviteeEmail);
        var agentInvitee = await ApiClient.RegisterAsync(factory, agentInviteeEmail);

        // owner2 is demoted (can no longer vouch for an Owner), the manager's account is deactivated
        (await owner1.PutAsync($"/api/v1/tenants/{org.Id}/members/{members.Single(m => m.UserId == owner2.UserId).Id}/role", new { role = "Manager" })).EnsureSuccessStatusCode();
        (await manager.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await ownerInvitee.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, ownerInviteeEmail) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agentInvitee.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, agentInviteeEmail) })).StatusCode);
        Assert.DoesNotContain(await owner1.MembersAsync(org.Id), m => m.UserId == ownerInvitee.UserId || m.UserId == agentInvitee.UserId);
    }

    [Fact]
    public async Task A_demoted_inviter_who_still_has_enough_authority_keeps_the_invitation_valid()
    {
        var (owner, org) = await NewTenantAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var email = $"still-valid-{Guid.NewGuid():N}@example.com";
        await InviteAsync(manager, org.Id, email, "Agent");
        var invitee = await ApiClient.RegisterAsync(factory, email);

        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, email) })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicate_invitations_yield_one_created_and_one_conflict_never_a_server_error()
    {
        var (owner, org) = await NewTenantAsync();
        var email = $"dup-race-{Guid.NewGuid():N}@example.com";

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email, role = "Agent" })));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).OrderBy(s => s).ToArray());
        Assert.Equal(1, await InvitationRowsAsync(email));
    }
}
