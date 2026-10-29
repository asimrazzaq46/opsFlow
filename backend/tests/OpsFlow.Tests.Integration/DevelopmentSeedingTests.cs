using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Infrastructure.Initialization;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>The development seeder, run directly (the Development startup hook itself is never active in test hosts).</summary>
public class DevelopmentSeedingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string StrongPassword = "Seed!Str0ng#Pass1";

    private static DevelopmentSeedOptions NewOptions(string? password = StrongPassword) => new()
    {
        AdminEmail = $"seed-{Guid.NewGuid():N}@example.com",
        OrganizationName = $"Demo {Guid.NewGuid():N}",
        AdminPassword = password,
    };

    private async Task RunAsync(DevelopmentSeedOptions options)
    {
        using var scope = factory.Services.CreateScope();
        var seeder = ActivatorUtilities.CreateInstance<DevelopmentAdminSeeder>(scope.ServiceProvider, Options.Create(options));
        await seeder.SeedAsync(CancellationToken.None);
    }

    private async Task<(int Users, int Tenants, int Memberships)> CountsAsync(DevelopmentSeedOptions options)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return (
            await db.Users.CountAsync(u => u.Email == options.AdminEmail),
            await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == options.OrganizationName),
            await db.Memberships.IgnoreQueryFilters().CountAsync(m => db.Users.Any(u => u.Id == m.UserId && u.Email == options.AdminEmail)));
    }

    [Fact]
    public async Task Seeder_creates_a_confirmed_admin_who_is_the_active_tenant_owner_and_can_sign_in()
    {
        var options = NewOptions();

        await RunAsync(options);

        Assert.Equal((1, 1, 1), await CountsAsync(options));
        var admin = factory.CreateApiClient();
        Assert.True((await admin.LoginAsync(options.AdminEmail, StrongPassword)).IsSuccessStatusCode);
        var orgs = await admin.ReadAsync<List<OrgDto>>(await admin.GetAsync("/api/v1/organizations"));
        Assert.Equal("TenantOwner", Assert.Single(orgs, o => o.Name == options.OrganizationName).Role);
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_anything()
    {
        var options = NewOptions();

        await RunAsync(options);
        await RunAsync(options);

        Assert.Equal((1, 1, 1), await CountsAsync(options));
    }

    [Fact]
    public async Task A_preexisting_unrelated_account_is_never_granted_access_or_re_passworded()
    {
        var stranger = await ApiClient.RegisterAsync(factory);
        var options = NewOptions();
        options.AdminEmail = stranger.Email;

        await RunAsync(options);

        Assert.Equal((1, 0, 0), await CountsAsync(options));
        Assert.True((await factory.CreateApiClient().LoginAsync(stranger.Email, ApiClient.Password)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().LoginAsync(stranger.Email, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_password_nothing_is_created()
    {
        var options = NewOptions(password: null);

        await RunAsync(options);

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }

    [Fact]
    public async Task A_weak_password_fails_clearly_and_leaves_nothing_behind()
    {
        var options = NewOptions("weak");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(options));

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }

    [PostgresOnlyFact]
    public async Task A_failure_after_the_user_was_created_rolls_the_whole_seed_back()
    {
        var options = NewOptions();
        options.OrganizationName = new string('x', 101); // exceeds Tenants.Name varchar(100) only on PostgreSQL

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => RunAsync(options));

        Assert.Equal((0, 0, 0), await CountsAsync(options));
    }
}
