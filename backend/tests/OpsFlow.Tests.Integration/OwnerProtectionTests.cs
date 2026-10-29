using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
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
