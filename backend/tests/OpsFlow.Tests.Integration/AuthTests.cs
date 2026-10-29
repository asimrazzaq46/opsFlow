using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Account lifecycle and access-token behaviour. Refresh/session behaviour lives in <see cref="SessionTests"/>.</summary>
public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string NewPassword = "An0ther!Passw0rd#2";

    private static async Task<string> TitleAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString()!;

    private Task<ApiClient> AnonymousAsync() => Task.FromResult(factory.CreateApiClient());

    private async Task<(ApiClient Client, string UserId, string Token)> RegisterUnverifiedAsync()
    {
        var email = $"pending-{Guid.NewGuid():N}@example.com";
        var client = await AnonymousAsync();
        (await client.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Pending" })).EnsureSuccessStatusCode();
        var (userId, token) = ApiClient.ParseLink(factory.Emails.Last(email, "/auth/confirm-email").Body);
        return (client, userId, token);
    }

    private async Task<string> ForgotAsync(ApiClient client, string email)
    {
        (await client.PostAsync("/api/v1/auth/forgot-password", new { email })).EnsureSuccessStatusCode();
        return ApiClient.ParseLink(factory.Emails.Last(email, "/auth/reset-password").Body).Token;
    }

    [Fact]
    public async Task Register_verify_login_and_me_work()
    {
        var client = await ApiClient.RegisterAsync(factory);

        var me = await client.ReadAsync<UserDto>(await client.GetAsync("/api/v1/auth/me"));

        Assert.Equal(client.Email, me.Email);
        Assert.Equal("Test User", me.DisplayName);
    }

    [Fact]
    public async Task Login_is_rejected_until_email_is_verified()
    {
        var email = $"unverified-{Guid.NewGuid():N}@example.com";
        var client = await AnonymousAsync();
        (await client.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Unverified" })).EnsureSuccessStatusCode();

        var login = await client.LoginAsync(email, ApiClient.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_account_fail_identically()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var anonymous = await AnonymousAsync();

        var wrong = await anonymous.LoginAsync(user.Email, "Wrong!Passw0rd#9");
        var unknown = await anonymous.LoginAsync($"nobody-{Guid.NewGuid():N}@example.com", ApiClient.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await TitleAsync(wrong), await TitleAsync(unknown));
    }

    [Fact]
    public async Task Token_responses_set_no_cookies_and_must_not_be_cached()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var raw = factory.CreateClient();

        var login = await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = ApiClient.Password });
        var auth = (await login.Content.ReadFromJsonAsync<AuthDto>())!;
        var refresh = await raw.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = auth.RefreshToken });

        foreach (var response in new[] { login, refresh })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }

        Assert.False(string.IsNullOrEmpty(auth.RefreshToken)); // delivered in the JSON body
    }

    [Fact]
    public async Task Refresh_token_is_only_accepted_from_the_json_body_never_from_a_cookie()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var raw = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Cookie", $"opsflow.refresh={user.RefreshToken}; refreshToken={user.RefreshToken}");

        var response = await raw.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode); // the real token in the body still works
    }

    [Fact]
    public async Task Protected_endpoints_reject_missing_forged_unsigned_and_expired_tokens()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var handler = new JsonWebTokenHandler();
        SecurityTokenDescriptor Descriptor(SigningCredentials? credentials) => new()
        {
            Issuer = "opsflow-api",
            Audience = "opsflow-app",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", user.UserId.ToString()), new Claim("sid", Guid.NewGuid().ToString()), new Claim("av", "1")]),
            SigningCredentials = credentials,
        };
        var forged = handler.CreateToken(Descriptor(new SigningCredentials(new SymmetricSecurityKey(new byte[48].Select((_, i) => (byte)(i + 1)).ToArray()), SecurityAlgorithms.HmacSha256)));
        var unsigned = handler.CreateToken(Descriptor(null));

        async Task<HttpStatusCode> WithAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return (await factory.CreateClient().SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await WithAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await WithAsync("not.a.jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, await WithAsync(forged));
        Assert.Equal(HttpStatusCode.Unauthorized, await WithAsync(unsigned));
        Assert.Equal(HttpStatusCode.OK, await WithAsync(user.AccessToken));

        factory.Clock.Advance(TimeSpan.FromMinutes(16)); // access tokens live 15 minutes
        Assert.Equal(HttpStatusCode.Unauthorized, await WithAsync(user.AccessToken));
        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Weak_password_returns_validation_problem()
    {
        var client = await AnonymousAsync();

        var response = await client.PostAsync("/api/v1/auth/register", new { email = "weak@example.com", password = "short", displayName = "Weak" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("errors", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_an_existing_email_does_not_reveal_it()
    {
        var existing = await ApiClient.RegisterAsync(factory);
        var client = await AnonymousAsync();

        var response = await client.PostAsync("/api/v1/auth/register", new { email = existing.Email, password = ApiClient.Password, displayName = "Dup" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicate_registrations_are_safe_and_create_one_account()
    {
        var email = $"race-{Guid.NewGuid():N}@example.com";
        var clients = new[] { await AnonymousAsync(), await AnonymousAsync(), await AnonymousAsync() };

        var results = await Task.WhenAll(clients.Select(c => c.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Racer" })));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Users.CountAsync(u => u.NormalizedEmail == email));
    }

    [Fact]
    public async Task Email_verification_link_is_single_use_and_expires()
    {
        var (client, userId, token) = await RegisterUnverifiedAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/confirm-email", new { userId, token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/confirm-email", new { userId, token })).StatusCode);

        var (late, lateUserId, lateToken) = await RegisterUnverifiedAsync();
        factory.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(HttpStatusCode.BadRequest, (await late.PostAsync("/api/v1/auth/confirm-email", new { userId = lateUserId, token = lateToken })).StatusCode);
    }

    [Fact]
    public async Task Verification_token_of_another_user_is_rejected()
    {
        var (client, _, token) = await RegisterUnverifiedAsync();
        var (_, otherUserId, _) = await RegisterUnverifiedAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/confirm-email", new { userId = otherUserId, token })).StatusCode);
    }

    [Fact]
    public async Task Password_reset_is_single_use_and_revokes_every_session()
    {
        var session = await ApiClient.RegisterAsync(factory);
        var anonymous = await AnonymousAsync();
        var token = await ForgotAsync(anonymous, session.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsync("/api/v1/auth/reset-password", new { email = session.Email, token, newPassword = NewPassword })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.RefreshAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await AnonymousAsync()).LoginAsync(session.Email, ApiClient.Password)).StatusCode);
        Assert.True((await (await AnonymousAsync()).LoginAsync(session.Email, NewPassword)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/api/v1/auth/reset-password", new { email = session.Email, token, newPassword = "Th1rd!Passw0rd#3" })).StatusCode);
    }

    [Fact]
    public async Task Password_reset_token_expires_and_unknown_email_looks_the_same()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var anonymous = await AnonymousAsync();
        var token = await ForgotAsync(anonymous, user.Email);
        factory.Clock.Advance(TimeSpan.FromMinutes(61));

        var expired = await anonymous.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword = NewPassword });
        var unknown = await anonymous.PostAsync("/api/v1/auth/reset-password", new { email = "nobody@example.com", token = "x", newPassword = NewPassword });
        var forgotUnknown = await anonymous.PostAsync("/api/v1/auth/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(await TitleAsync(expired), await TitleAsync(unknown));
        Assert.Equal(HttpStatusCode.Accepted, forgotUnknown.StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_replaces_all_sessions_and_keeps_the_caller_signed_in()
    {
        var here = await ApiClient.RegisterAsync(factory);
        var elsewhere = factory.CreateApiClient();
        await elsewhere.LoginAsync(here.Email, ApiClient.Password);

        Assert.Equal(HttpStatusCode.BadRequest, (await here.ChangePasswordAsync("Wrong!Passw0rd#9", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.ChangePasswordAsync(ApiClient.Password, NewPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/v1/auth/me")).StatusCode); // fresh session from the response
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.RefreshAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await AnonymousAsync()).LoginAsync(here.Email, ApiClient.Password)).StatusCode);
    }

    [Fact]
    public async Task Repeated_failed_logins_lock_the_account_until_the_lockout_expires()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var anonymous = await AnonymousAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(user.Email, "Wrong!Passw0rd#9")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(user.Email, ApiClient.Password)).StatusCode); // correct password, still locked
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await anonymous.LoginAsync(user.Email, ApiClient.Password)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Deactivation_revokes_everything_and_blocks_sign_in()
    {
        var user = await ApiClient.RegisterAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/v1/auth/deactivate", new { password = "Wrong!Passw0rd#9" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await AnonymousAsync()).LoginAsync(user.Email, ApiClient.Password)).StatusCode);
    }

    [Fact]
    public async Task The_only_active_owner_cannot_deactivate_until_another_owner_exists()
    {
        var owner = await ApiClient.RegisterAsync(factory);
        var org = await owner.CreateOrganizationAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);

        await ApiClient.AddMemberAsync(factory, owner, org.Id, "TenantOwner");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);
    }

    [Fact]
    public async Task Unrelated_persistence_failures_during_registration_are_not_reported_as_accepted()
    {
        var email = $"fault-{Guid.NewGuid():N}@example.com";
        var client = await AnonymousAsync();
        factory.Faults.FailNextSave();

        var response = await client.PostAsync("/api/v1/auth/register", new { email, password = ApiClient.Password, displayName = "Faulty" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("injected", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>().Users.AnyAsync(u => u.NormalizedEmail == email));
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
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        var now = DateTime.UtcNow;

        async Task<DbUpdateException> FailAsync()
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            return ex;
        }

        db.Users.Add(User.Create(email, "x", "hash", false, now));
        await db.SaveChangesAsync();
        db.Users.Add(User.Create(email.ToUpperInvariant(), "x", "hash", false, now));
        var duplicateUser = await FailAsync();

        using (tenantContext.Begin(org.Id))
        {
            db.Memberships.Add(Membership.Create(org.Id, owner.UserId, TenantRole.Agent, now));
            var duplicateMembership = await FailAsync();
            db.Memberships.Add(Membership.Create(org.Id, Guid.NewGuid(), TenantRole.Agent, now));
            var foreignKeyFailure = await FailAsync();

            Assert.True(db.IsUniqueViolation(duplicateMembership, UniqueConstraintKind.MembershipUser));
            Assert.False(db.IsUniqueViolation(duplicateMembership, UniqueConstraintKind.UserEmail));
            Assert.False(db.IsUniqueViolation(foreignKeyFailure, UniqueConstraintKind.MembershipUser));
        }

        var duplicateSlugTenant = Guid.NewGuid();
        using (tenantContext.Begin(duplicateSlugTenant))
        {
            db.Tenants.Add(Tenant.Create(duplicateSlugTenant, "Dup", org.Slug, now));
            Assert.True(db.IsUniqueViolation(await FailAsync(), UniqueConstraintKind.TenantSlug));
        }

        Assert.True(db.IsUniqueViolation(duplicateUser, UniqueConstraintKind.UserEmail));
        Assert.False(db.IsUniqueViolation(duplicateUser, UniqueConstraintKind.TenantSlug));
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

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsync("/api/v1/auth/login", new { email = "x@example.com", password = "nope" })).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
