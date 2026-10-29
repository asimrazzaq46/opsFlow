using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
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
}
