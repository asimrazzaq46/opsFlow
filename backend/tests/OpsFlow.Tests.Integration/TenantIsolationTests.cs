using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public class TenantIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Creating_an_organization_makes_the_caller_its_first_owner()
    {
        var user = await ApiClient.RegisterAsync(factory);

        var suffix = Guid.NewGuid().ToString("N");
        var org = await user.CreateOrganizationAsync($"Acme & Support {suffix}");

        Assert.Equal("TenantOwner", org.Role);
        Assert.Equal($"acme-support-{suffix}", org.Slug);
        var mine = await user.ReadAsync<List<OrgDto>>(await user.GetAsync("/api/v1/organizations"));
        Assert.Contains(mine, o => o.Id == org.Id);
        var members = await user.MembersAsync(org.Id);
        Assert.Single(members, m => m.UserId == user.UserId && m.Role == "TenantOwner" && m.IsActive);
    }

    [Fact]
    public async Task Organization_with_a_taken_name_gets_a_unique_slug()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var first = await user.CreateOrganizationAsync("Same Name");

        var second = await user.CreateOrganizationAsync("Same Name");

        Assert.NotEqual(first.Slug, second.Slug);
    }

    [Fact]
    public async Task Non_members_cannot_reach_another_tenant()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var outsider = await ApiClient.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/tenants/{org.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/tenants/{org.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "x@example.com", role = "Agent" })).StatusCode);
        Assert.Empty(await outsider.ReadAsync<List<OrgDto>>(await outsider.GetAsync("/api/v1/organizations")));
    }

    [Fact]
    public async Task Members_of_one_tenant_cannot_use_their_membership_in_another()
    {
        var ownerA = await ApiClient.RegisterAsync(factory);
        var orgA = await ownerA.CreateOrganizationAsync("Tenant A");
        var ownerB = await ApiClient.RegisterAsync(factory);
        var orgB = await ownerB.CreateOrganizationAsync("Tenant B");
        var memberOfA = await ownerA.MembersAsync(orgA.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await ownerB.PutAsync($"/api/v1/tenants/{orgA.Id}/members/{memberOfA[0].Id}/role", new { role = "Agent" })).StatusCode);
        // Using a membership id of tenant A under tenant B's route must not touch tenant A's row.
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PutAsync($"/api/v1/tenants/{orgB.Id}/members/{memberOfA[0].Id}/role", new { role = "Agent" })).StatusCode);
        Assert.Equal("TenantOwner", (await ownerA.MembersAsync(orgA.Id))[0].Role);
    }

    [Fact]
    public async Task Role_limits_are_enforced()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var agent = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Agent");

        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/v1/tenants/{org.Id}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "a@example.com", role = "Agent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsync($"/api/v1/tenants/{org.Id}/members/{(await owner.MembersAsync(org.Id))[0].Id}/role", new { role = "Agent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "new-agent@example.com", role = "Agent" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/v1/tenants/{org.Id}/invitations", new { email = "new-manager@example.com", role = "Manager" })).StatusCode);
    }

    [Fact]
    public async Task One_user_can_hold_different_roles_in_several_organizations()
    {
        var ownerA = await ApiClient.RegisterAsync(factory);
        var orgA = await ownerA.CreateOrganizationAsync($"Multi A {Guid.NewGuid():N}");
        var ownerB = await ApiClient.RegisterAsync(factory);
        var orgB = await ownerB.CreateOrganizationAsync($"Multi B {Guid.NewGuid():N}");
        var user = await ApiClient.AddMemberAsync(factory, ownerA, orgA.Id, "Manager");
        var email = user.Email;
        (await ownerB.PostAsync($"/api/v1/tenants/{orgB.Id}/invitations", new { email, role = "Agent" })).EnsureSuccessStatusCode();
        (await user.PostAsync("/api/v1/invitations/accept", new { token = ApiClient.InvitationTokenFrom(factory, email) })).EnsureSuccessStatusCode();

        var mine = await user.ReadAsync<List<OrgDto>>(await user.GetAsync("/api/v1/organizations"));

        // the SAME access token works in both tenants, but the role is decided per tenant by the database
        Assert.Equal("Manager", mine.Single(o => o.Id == orgA.Id).Role);
        Assert.Equal("Agent", mine.Single(o => o.Id == orgB.Id).Role);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/v1/tenants/{orgA.Id}/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/v1/tenants/{orgB.Id}/invitations")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_members_lose_access_immediately()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var agent = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Agent");
        var agentMembership = (await owner.MembersAsync(org.Id)).Single(m => m.UserId == agent.UserId);

        (await owner.PutAsync($"/api/v1/tenants/{org.Id}/members/{agentMembership.Id}/status", new { isActive = false })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/v1/tenants/{org.Id}/members")).StatusCode);
        Assert.Empty(await agent.ReadAsync<List<OrgDto>>(await agent.GetAsync("/api/v1/organizations")));
    }

    [Fact]
    public async Task Role_changes_take_effect_on_the_next_request()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();
        var manager = await ApiClient.AddMemberAsync(factory, owner, org.Id, "Manager");
        var membership = (await owner.MembersAsync(org.Id)).Single(m => m.UserId == manager.UserId);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/v1/tenants/{org.Id}/invitations")).StatusCode);

        (await owner.PutAsync($"/api/v1/tenants/{org.Id}/members/{membership.Id}/role", new { role = "Agent" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/tenants/{org.Id}/invitations")).StatusCode);
    }

    [Fact]
    public async Task Failed_provisioning_rolls_back_the_tenant()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tenantId = Guid.NewGuid();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContext>().Begin(tenantId);
        db.Tenants.Add(Tenant.Create(tenantId, "Rollback", $"rollback-{tenantId:N}", DateTime.UtcNow));
        db.Memberships.Add(Membership.Create(tenantId, Guid.NewGuid() /* no such user -> FK violation */, TenantRole.TenantOwner, DateTime.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        Assert.False(await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId));
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_and_fails_closed_without_a_tenant()
    {
        var ownerA = await ApiClient.RegisterAsync(factory);
        var orgA = await ownerA.CreateOrganizationAsync("Filter A");
        var ownerB = await ApiClient.RegisterAsync(factory);
        var orgB = await ownerB.CreateOrganizationAsync("Filter B");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        Assert.Equal(0, await db.Memberships.CountAsync());
        Assert.Equal(0, await db.Tenants.CountAsync());
        using (tenantContext.Begin(orgA.Id))
        {
            Assert.All(await db.Memberships.ToListAsync(), m => Assert.Equal(orgA.Id, m.TenantId));
            Assert.Equal(orgA.Id, (await db.Tenants.SingleAsync()).Id);
        }

        Assert.True(await db.Memberships.IgnoreQueryFilters().AnyAsync(m => m.TenantId == orgB.Id));
    }

    [Fact]
    public async Task Writes_outside_the_current_tenant_scope_are_blocked()
    {
        var ownerA = await ApiClient.RegisterAsync(factory);
        var orgA = await ownerA.CreateOrganizationAsync("Write A");
        var ownerB = await ApiClient.RegisterAsync(factory);
        var orgB = await ownerB.CreateOrganizationAsync("Write B");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // no tenant scope at all
        db.Memberships.Add(Membership.Create(orgA.Id, Guid.NewGuid(), TenantRole.Agent, DateTime.UtcNow));
        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        using (tenantContext.Begin(orgA.Id))
        {
            // adding a row for another tenant
            db.Memberships.Add(Membership.Create(orgB.Id, Guid.NewGuid(), TenantRole.Agent, DateTime.UtcNow));
            await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            // modifying a row that belongs to another tenant (loaded by bypassing the filter)
            var foreign = await db.Memberships.IgnoreQueryFilters().FirstAsync(m => m.TenantId == orgB.Id);
            foreign.ChangeRole(TenantRole.Agent, DateTime.UtcNow);
            await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            // rewriting TenantId of an own row: EF itself refuses for key properties (Membership)...
            var own = await db.Memberships.FirstAsync();
            Assert.Throws<InvalidOperationException>(() => db.Entry(own).Property(nameof(ITenantOwned.TenantId)).CurrentValue = orgB.Id);
            db.ChangeTracker.Clear();

            // ...and our SaveChanges guard catches it for non-key tenant columns (Invitation)
            var invitation = Invitation.Create(orgA.Id, "move@example.com", TenantRole.Agent, "move-hash", (await db.Memberships.FirstAsync()).Id, DateTime.UtcNow, TimeSpan.FromHours(72));
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync();
            db.Entry(invitation).Property(nameof(ITenantOwned.TenantId)).CurrentValue = orgB.Id;
            await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Model;

        var unfiltered = model.GetEntityTypes()
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name);

        Assert.Empty(unfiltered);
    }
}
