using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Register_confirm_login_and_me_work()
    {
        var client = await ApiClient.RegisterAsync(factory);

        var me = await client.ReadAsync<UserDto>(await client.GetAsync("/api/v1/auth/me"));

        Assert.Equal(client.Email, me.Email);
        Assert.Equal("Test User", me.DisplayName);
    }

    [Fact]
    public async Task Login_is_rejected_until_email_is_confirmed()
    {
        var email = $"unconfirmed-{Guid.NewGuid():N}@example.com";
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync();
        (await client.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Un Confirmed" })).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(email, ApiClient.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var client = await ApiClient.RegisterAsync(factory);

        var login = await factory.CreateApiClient().LoginAsync(client.Email, "Wrong!Passw0rd#9");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Unsafe_request_without_csrf_token_is_rejected()
    {
        var client = factory.CreateApiClient();

        var response = await client.PostAsync("/api/v1/auth/register", new { email = "a@example.com", password = ApiClient.Password, displayName = "No Csrf" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_requires_authentication()
    {
        var response = await factory.CreateApiClient().GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Weak_password_returns_validation_problem()
    {
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync();

        var response = await client.PostAsync("/api/v1/auth/register", new { email = "weak@example.com", password = "short", displayName = "Weak" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registering_an_existing_email_does_not_reveal_it()
    {
        var existing = await ApiClient.RegisterAsync(factory);
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync();

        var response = await client.PostAsync("/api/v1/auth/register", new { email = existing.Email, password = ApiClient.Password, displayName = "Dup" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Password_reset_allows_login_with_new_password_only()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync();
        (await client.PostAsync("/api/v1/auth/forgot-password", new { email = user.Email })).EnsureSuccessStatusCode();
        var (_, token) = ApiClient.ParseLink(factory.Emails.Last(user.Email, "/auth/reset-password").Body);
        const string newPassword = "An0ther!Passw0rd#2";

        var reset = await client.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().LoginAsync(user.Email, ApiClient.Password)).StatusCode);
        Assert.True((await factory.CreateApiClient().LoginAsync(user.Email, newPassword)).IsSuccessStatusCode);

        var replay = await client.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword = "Th1rd!Passw0rd#3" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = await ApiClient.RegisterAsync(factory);

        (await client.PostAsync("/api/v1/auth/logout")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Password_reset_invalidates_existing_sessions()
    {
        var session = await ApiClient.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        var other = factory.CreateApiClient();
        await other.RefreshCsrfAsync();
        (await other.PostAsync("/api/v1/auth/forgot-password", new { email = session.Email })).EnsureSuccessStatusCode();
        var (_, token) = ApiClient.ParseLink(factory.Emails.Last(session.Email, "/auth/reset-password").Body);
        (await other.PostAsync("/api/v1/auth/reset-password", new { email = session.Email, token, newPassword = "An0ther!Passw0rd#2" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Locked_out_account_loses_its_existing_session_immediately()
    {
        var session = await ApiClient.RegisterAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(session.Email))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicate_registrations_are_safe_and_create_one_account()
    {
        var email = $"race-{Guid.NewGuid():N}@example.com";
        var clients = new[] { factory.CreateApiClient(), factory.CreateApiClient(), factory.CreateApiClient() };
        foreach (var c in clients)
        {
            await c.RefreshCsrfAsync();
        }

        var results = await Task.WhenAll(clients.Select(c => c.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Racer" })));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Users.CountAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Unrelated_persistence_failures_during_registration_are_not_reported_as_accepted()
    {
        var email = $"fault-{Guid.NewGuid():N}@example.com";
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync();
        factory.Faults.FailNextSave();

        var response = await client.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Faulty" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("injected", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Unrelated_persistence_failures_creating_an_organization_are_not_reported_as_conflicts()
    {
        var user = await ApiClient.RegisterAsync(factory);
        factory.Faults.FailNextSave();

        var response = await user.PostAsync("/api/v1/organizations", new { name = "Faulty Org" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_expected_unique_constraint_is_recognised_as_a_duplicate()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync($"Unique {Guid.NewGuid():N}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<OpsFlow.Application.Abstractions.ITenantContext>();
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        async Task<DbUpdateException> FailAsync()
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            return ex;
        }

        ApplicationUser NewUser(string normalized) => new() { Id = Guid.NewGuid(), UserName = email, NormalizedUserName = normalized, Email = email, NormalizedEmail = normalized, DisplayName = "x" };

        db.Users.Add(NewUser(email.ToUpperInvariant()));
        await db.SaveChangesAsync();
        db.Users.Add(NewUser(email.ToUpperInvariant()));
        var duplicateUser = await FailAsync();

        using (tenantContext.Begin(org.Id))
        {
            db.Memberships.Add(OpsFlow.Domain.Tenants.Membership.Create(org.Id, owner.UserId, OpsFlow.Domain.Tenants.TenantRole.Agent, DateTime.UtcNow));
            var duplicateMembership = await FailAsync();
            db.Memberships.Add(OpsFlow.Domain.Tenants.Membership.Create(org.Id, Guid.NewGuid(), OpsFlow.Domain.Tenants.TenantRole.Agent, DateTime.UtcNow));
            var foreignKeyFailure = await FailAsync();

            Assert.True(db.IsUniqueViolation(duplicateMembership, OpsFlow.Application.Abstractions.UniqueConstraintKind.MembershipUser));
            Assert.False(db.IsUniqueViolation(duplicateMembership, OpsFlow.Application.Abstractions.UniqueConstraintKind.UserName));
            Assert.False(db.IsUniqueViolation(foreignKeyFailure, OpsFlow.Application.Abstractions.UniqueConstraintKind.MembershipUser));
        }

        var duplicateSlugTenant = Guid.NewGuid();
        using (tenantContext.Begin(duplicateSlugTenant))
        {
            db.Tenants.Add(OpsFlow.Domain.Tenants.Tenant.Create(duplicateSlugTenant, "Dup", org.Slug, DateTime.UtcNow));
            var duplicateSlug = await FailAsync();
            Assert.True(db.IsUniqueViolation(duplicateSlug, OpsFlow.Application.Abstractions.UniqueConstraintKind.TenantSlug));
        }

        Assert.True(db.IsUniqueViolation(duplicateUser, OpsFlow.Application.Abstractions.UniqueConstraintKind.UserName));
        Assert.False(db.IsUniqueViolation(duplicateUser, OpsFlow.Application.Abstractions.UniqueConstraintKind.TenantSlug));
    }

    [Fact]
    public async Task Health_stays_public()
    {
        var response = await factory.CreateApiClient().GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class RateLimitTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task Sensitive_endpoints_are_rate_limited()
    {
        var client = factory.CreateApiClient();
        await client.RefreshCsrfAsync(); // consumes one permit of the same window

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsync("/api/v1/auth/login", new { email = "x@example.com", password = "nope" })).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
