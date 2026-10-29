using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public class OwnerProtectionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string RoleUrl(Guid tenantId, Guid membershipId) => $"/api/v1/tenants/{tenantId}/members/{membershipId}/role";

    private static string StatusUrl(Guid tenantId, Guid membershipId) => $"/api/v1/tenants/{tenantId}/members/{membershipId}/status";

    private async Task<int> ActiveOwnersAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Memberships.IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.Role == TenantRole.TenantOwner && m.Status == MembershipStatus.Active);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_or_deactivated()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var self = (await owner.MembersAsync(org.Id)).Single();

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync(RoleUrl(org.Id, self.Id), new { role = "Manager" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync(StatusUrl(org.Id, self.Id), new { isActive = false })).StatusCode);
        Assert.Equal(1, await ActiveOwnersAsync(org.Id));
    }

    [Fact]
    public async Task An_owner_can_step_down_when_another_owner_exists()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var second = await ApiClient.AddMemberAsync(factory, owner, org.Id, "TenantOwner");
        var secondMembership = (await owner.MembersAsync(org.Id)).Single(m => m.UserId == second.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(RoleUrl(org.Id, secondMembership.Id), new { role = "Manager" })).StatusCode);
        Assert.Equal(1, await ActiveOwnersAsync(org.Id));
    }

    [Fact]
    public async Task Concurrent_owner_removals_never_leave_the_tenant_without_an_owner()
    {
        for (var round = 0; round < 5; round++)
        {
            var first = await ApiClient.RegisterAsync(factory);
            var org = await first.CreateOrganizationAsync($"Race {round}");
            var second = await ApiClient.AddMemberAsync(factory, first, org.Id, "TenantOwner");
            var members = await first.MembersAsync(org.Id);
            var firstMembership = members.Single(m => m.UserId == first.UserId);
            var secondMembership = members.Single(m => m.UserId == second.UserId);

            // each owner demotes the other at the same time
            var results = await Task.WhenAll(
                first.PutAsync(RoleUrl(org.Id, secondMembership.Id), new { role = "Agent" }),
                second.PutAsync(RoleUrl(org.Id, firstMembership.Id), new { role = "Agent" }));

            var owners = await ActiveOwnersAsync(org.Id);
            var statuses = string.Join(",", results.Select(r => (int)r.StatusCode));
            Assert.True(owners == 1 && results.Count(r => r.StatusCode == HttpStatusCode.NoContent) == 1, $"round {round}: statuses [{statuses}], active owners {owners}");
        }
    }

    private async Task<(OrgDto Org, Guid FirstOwner, Guid SecondOwner)> TwoOwnersAsync(string name)
    {
        var first = await ApiClient.RegisterAsync(factory);
        var org = await first.CreateOrganizationAsync(name);
        var second = await ApiClient.AddMemberAsync(factory, first, org.Id, "TenantOwner");
        var members = await first.MembersAsync(org.Id);
        return (org, members.Single(m => m.UserId == first.UserId).Id, members.Single(m => m.UserId == second.UserId).Id);
    }

    private (MembershipService Service, OpsFlowDbContext Db, IServiceScope Scope) ServiceAs(Guid tenantId, Guid actorMembershipId)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, actorMembershipId, TenantRole.TenantOwner);
        return (scope.ServiceProvider.GetRequiredService<MembershipService>(), scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope);
    }

    private async Task<(Guid Stamp, TenantRole Role)> StateAsync(Guid tenantId, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var stamp = await db.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.OwnerChangeStamp).SingleAsync();
        var role = await db.Memberships.IgnoreQueryFilters().Where(m => m.Id == membershipId).Select(m => m.Role).SingleAsync();
        return (stamp, role);
    }

    [PostgresOnlyFact]
    public async Task Stale_demotion_is_rolled_back_and_its_retry_rechecks_the_actors_authorization()
    {
        var (org, owner1, owner2) = await TwoOwnersAsync("Interleave authz");
        var (winner, _, winnerScope) = ServiceAs(org.Id, owner1);
        var (loser, loserDb, loserScope) = ServiceAs(org.Id, owner2);
        using (winnerScope)
        using (loserScope)
        {
            var before = await StateAsync(org.Id, owner1);

            // owner2 starts demoting owner1 and is frozen after reading stamp + owner count (both still look fine)
            factory.Gate.PauseAfterNextCount(loserDb);
            var losing = Task.Run(() => loser.ChangeRoleAsync(owner1, TenantRole.Agent, CancellationToken.None));
            Assert.True(await factory.Gate.WaitUntilPausedAsync());

            // meanwhile owner1 demotes owner2 and commits: membership change + stamp rotation in ONE transaction
            await winner.ChangeRoleAsync(owner2, TenantRole.Agent, CancellationToken.None);
            var afterWinner = await StateAsync(org.Id, owner2);
            Assert.Equal(TenantRole.Agent, afterWinner.Role);
            Assert.NotEqual(before.Stamp, afterWinner.Stamp);

            factory.Gate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout));

            // the stale save hit the stamp, rolled back completely, and the retry saw owner2 is no longer an Owner
            Assert.Equal(403, failure.StatusCode);
            var after = await StateAsync(org.Id, owner1);
            Assert.Equal(TenantRole.TenantOwner, after.Role);
            Assert.Equal(afterWinner.Stamp, after.Stamp);
            Assert.Equal(1, await ActiveOwnersAsync(org.Id));
        }
    }

    [PostgresOnlyFact]
    public async Task Stale_demotion_retry_rechecks_the_current_owner_count()
    {
        var (org, owner1, owner2) = await TwoOwnersAsync("Interleave count");
        var (winner, _, winnerScope) = ServiceAs(org.Id, owner1);
        var (loser, loserDb, loserScope) = ServiceAs(org.Id, owner1);
        using (winnerScope)
        using (loserScope)
        {
            // owner1 starts stepping down (owner2 still counts as the other Owner) and is frozen
            factory.Gate.PauseAfterNextCount(loserDb);
            var stepDown = Task.Run(() => loser.ChangeRoleAsync(owner1, TenantRole.Manager, CancellationToken.None));
            Assert.True(await factory.Gate.WaitUntilPausedAsync());

            // owner1 (another request) demotes owner2 first: owner1 becomes the last Owner
            await winner.ChangeRoleAsync(owner2, TenantRole.Agent, CancellationToken.None);
            var afterWinner = await StateAsync(org.Id, owner2);

            factory.Gate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => stepDown.WaitAsync(QueryGate.Timeout));

            Assert.Equal(409, failure.StatusCode);
            var after = await StateAsync(org.Id, owner1);
            Assert.Equal(TenantRole.TenantOwner, after.Role);
            Assert.Equal(afterWinner.Stamp, after.Stamp);
            Assert.Equal(1, await ActiveOwnersAsync(org.Id));
        }
    }

    [Fact]
    public async Task Owner_change_stamp_rejects_a_stale_concurrent_write()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync("Stamp");

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        using var ctxA = scopeA.ServiceProvider.GetRequiredService<ITenantContext>().Begin(org.Id);
        using var ctxB = scopeB.ServiceProvider.GetRequiredService<ITenantContext>().Begin(org.Id);

        var tenantA = await dbA.Tenants.SingleAsync();
        var tenantB = await dbB.Tenants.SingleAsync();
        tenantA.RotateOwnerChangeStamp();
        tenantB.RotateOwnerChangeStamp();

        await dbA.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    // ---- usable-owner counting + actor authorization at the commit boundary ----

    private sealed record Fixture(OrgDto Org, ApiClient Owner1, ApiClient Owner2, Guid Membership1, Guid Membership2);

    private async Task<Fixture> OwnersFixtureAsync(string name)
    {
        var first = await ApiClient.RegisterAsync(factory);
        var org = await first.CreateOrganizationAsync($"{name} {Guid.NewGuid():N}");
        var second = await ApiClient.AddMemberAsync(factory, first, org.Id, "TenantOwner");
        var members = await first.MembersAsync(org.Id);
        return new Fixture(org, first, second, members.Single(m => m.UserId == first.UserId).Id, members.Single(m => m.UserId == second.UserId).Id);
    }

    private async Task<Guid> AgentAsync(Fixture f)
    {
        var agent = await ApiClient.AddMemberAsync(factory, f.Owner1, f.Org.Id, "Agent");
        return (await f.Owner1.MembersAsync(f.Org.Id)).Single(m => m.UserId == agent.UserId).Id;
    }

    private async Task<int> UsableOwnersAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().UsableOwners(allTenants: true).CountAsync(m => m.TenantId == tenantId);
    }

    private (AccountService Service, IServiceScope Scope, OpsFlowDbContext Db) AccountScope()
    {
        var scope = factory.Services.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<AccountService>(), scope, scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>());
    }

    [Fact]
    public async Task A_deactivated_owner_does_not_count_as_a_usable_owner()
    {
        var f = await OwnersFixtureAsync("Usable owners");

        Assert.Equal(HttpStatusCode.NoContent, (await f.Owner2.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);

        Assert.Equal(1, await UsableOwnersAsync(f.Org.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner1.PutAsync(RoleUrl(f.Org.Id, f.Membership1), new { role = "Manager" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner1.PutAsync(StatusUrl(f.Org.Id, f.Membership1), new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner1.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Owner1.GetAsync("/api/v1/auth/me")).StatusCode); // the rejected deactivation changed nothing
    }

    [PostgresOnlyFact]
    public async Task Concurrent_deactivation_of_the_last_two_owners_leaves_exactly_one_usable_owner()
    {
        var f = await OwnersFixtureAsync("Concurrent deactivation");
        var (accounts1, scope1, db1) = AccountScope();
        var (accounts2, scope2, _) = AccountScope();
        using (scope1)
        using (scope2)
        {
            // owner1 passes the "another usable owner exists" check, then freezes
            factory.Gate.PauseAfterNextQuery(db1, "SELECT EXISTS");
            var first = Task.Run(() => accounts1.DeactivateAsync(f.Owner1.UserId, ApiClient.Password, CancellationToken.None));
            Assert.True(await factory.Gate.WaitUntilPausedAsync());

            await accounts2.DeactivateAsync(f.Owner2.UserId, ApiClient.Password, CancellationToken.None); // commits first
            factory.Gate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => first.WaitAsync(QueryGate.Timeout));

            Assert.Equal(409, failure.StatusCode); // the retry re-counted and saw owner2 gone
        }

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(1, await UsableOwnersAsync(f.Org.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == f.Owner1.UserId && u.Status == UserStatus.Active)); // fully rolled back
        Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == f.Owner1.UserId && s.RevokedAtUtc != null));
        Assert.True(await db.Users.AnyAsync(u => u.Id == f.Owner2.UserId && u.Status == UserStatus.Deactivated));
    }

    [Fact]
    public async Task A_promotion_by_an_actor_who_was_demoted_meanwhile_is_rejected_and_rolled_back()
    {
        var f = await OwnersFixtureAsync("Stale promotion");
        var agentId = await AgentAsync(f);
        var (loser, loserDb, loserScope) = ServiceAs(f.Org.Id, f.Membership2);
        var (winner, _, winnerScope) = ServiceAs(f.Org.Id, f.Membership1);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => loser.ChangeRoleAsync(agentId, TenantRole.TenantOwner, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await winner.ChangeRoleAsync(f.Membership2, TenantRole.Agent, CancellationToken.None); // the actor loses Owner
            var stampAfterWinner = (await StateAsync(f.Org.Id, agentId)).Stamp;
            factory.SaveGate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout));

            Assert.Equal(403, failure.StatusCode);
            var after = await StateAsync(f.Org.Id, agentId);
            Assert.Equal(TenantRole.Agent, after.Role); // the stale promotion did not commit
            Assert.Equal(stampAfterWinner, after.Stamp);
        }
    }

    [Fact]
    public async Task A_reactivation_by_an_actor_who_was_suspended_meanwhile_is_rejected()
    {
        var f = await OwnersFixtureAsync("Stale reactivation");
        var suspendedId = await AgentAsync(f);
        (await f.Owner1.PutAsync(StatusUrl(f.Org.Id, suspendedId), new { isActive = false })).EnsureSuccessStatusCode();
        var (loser, loserDb, loserScope) = ServiceAs(f.Org.Id, f.Membership2);
        var (winner, _, winnerScope) = ServiceAs(f.Org.Id, f.Membership1);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => loser.SetActiveAsync(suspendedId, true, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await winner.SetActiveAsync(f.Membership2, false, CancellationToken.None); // the actor is suspended
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout))).StatusCode);
            Assert.False((await f.Owner1.MembersAsync(f.Org.Id)).Single(m => m.Id == suspendedId).IsActive);
        }
    }

    [Fact]
    public async Task A_mutation_by_an_actor_whose_account_was_deactivated_meanwhile_is_rejected()
    {
        var f = await OwnersFixtureAsync("Deactivated actor");
        var agentId = await AgentAsync(f);
        var (loser, loserDb, loserScope) = ServiceAs(f.Org.Id, f.Membership2);
        using (loserScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => loser.ChangeRoleAsync(agentId, TenantRole.Manager, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            Assert.Equal(HttpStatusCode.NoContent, (await f.Owner2.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);
            factory.SaveGate.Resume();

            Assert.Equal(403, (await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout))).StatusCode);
            Assert.Equal(TenantRole.Agent, (await StateAsync(f.Org.Id, agentId)).Role);
        }
    }

    [Fact]
    public async Task Concurrent_mutations_by_the_same_still_authorized_actor_both_succeed_after_a_retry()
    {
        var f = await OwnersFixtureAsync("Both succeed");
        var first = await AgentAsync(f);
        var second = await AgentAsync(f);
        var (loser, loserDb, loserScope) = ServiceAs(f.Org.Id, f.Membership1);
        var (winner, _, winnerScope) = ServiceAs(f.Org.Id, f.Membership1);
        using (loserScope)
        using (winnerScope)
        {
            factory.SaveGate.PauseNextSave(loserDb);
            var losing = Task.Run(() => loser.ChangeRoleAsync(first, TenantRole.Manager, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            await winner.ChangeRoleAsync(second, TenantRole.Manager, CancellationToken.None);
            factory.SaveGate.Resume();
            await losing.WaitAsync(QueryGate.Timeout); // conflicts on the stamp, retries, succeeds: no spurious failure

            Assert.Equal(TenantRole.Manager, (await StateAsync(f.Org.Id, first)).Role);
            Assert.Equal(TenantRole.Manager, (await StateAsync(f.Org.Id, second)).Role);
        }
    }
}
