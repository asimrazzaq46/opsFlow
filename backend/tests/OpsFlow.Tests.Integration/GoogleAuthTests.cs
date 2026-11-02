using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Auth;
using OpsFlow.Domain.Users;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>
/// Google sign-in, registration and linking against the real API with a fake ID-token validator (no calls to Google). Cryptographic
/// checks (signature, issuer, audience, expiry) are delegated to Google.Apis.Auth; here a token failing any of them is an unknown credential.
/// </summary>
public class GoogleAuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Challenge(string ChallengeId, string Nonce, DateTime ExpiresAtUtc, string ClientId);

    private sealed record Profile(Guid Id, string Email, string DisplayName, bool HasPassword, bool GoogleLinked);

    private static string Sub() => $"google-sub-{Guid.NewGuid():N}";

    private static string Gmail() => $"g{Guid.NewGuid():N}@gmail.com";

    private static async Task<Challenge> ChallengeAsync(HttpClient http) =>
        (await (await http.PostAsJsonAsync("/api/v1/auth/google/challenge", new { })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Challenge>())!;

    private static Task<HttpResponseMessage> PostGoogleAsync(HttpClient http, string credential, string challengeId) =>
        http.PostAsJsonAsync("/api/v1/auth/google", new { credential, challengeId });

    private async Task<HttpResponseMessage> GoogleSignInAsync(string sub, string email, bool verified = true, string? hostedDomain = null)
    {
        var http = factory.CreateClient();
        var challenge = await ChallengeAsync(http);
        var credential = factory.Google.Issue(new GoogleIdentity(sub, email, verified, hostedDomain, challenge.Nonce, "Gina Google"));
        return await PostGoogleAsync(http, credential, challenge.ChallengeId);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    private HttpClient Bearer(string accessToken)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    private async Task<T> QueryAsync<T>(Func<OpsFlowDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>());
    }

    private async Task<string> LinkChallengeAndCredentialAsync(HttpClient http, string sub, string email)
    {
        var challenge = (await (await http.PostAsJsonAsync("/api/v1/auth/google/link/challenge", new { password = ApiClient.Password })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<Challenge>())!;
        var credential = factory.Google.Issue(new GoogleIdentity(sub, email, true, null, challenge.Nonce, null));
        return JsonSerializer.Serialize(new { credential, challengeId = challenge.ChallengeId });
    }

    private static Task<HttpResponseMessage> PostLinkAsync(HttpClient http, string body) =>
        http.PostAsync("/api/v1/auth/google/link", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

    // ---------------------------------------------------------------- sign-in / registration

    [Fact]
    public async Task New_gmail_user_is_registered_and_signed_in_without_any_membership_or_password()
    {
        var email = Gmail();
        var response = await GoogleSignInAsync(Sub(), email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var auth = (await response.Content.ReadFromJsonAsync<AuthDto>())!;
        Assert.Equal(email, auth.User.Email);
        Assert.Equal("Gina Google", auth.User.DisplayName);

        var http = Bearer(auth.AccessToken);
        Assert.Empty((await http.GetFromJsonAsync<List<OrgDto>>("/api/v1/organizations"))!); // no tenant or role is ever granted
        var profile = (await http.GetFromJsonAsync<Profile>("/api/v1/auth/me"))!;
        Assert.False(profile.HasPassword);
        Assert.True(profile.GoogleLinked);

        // Existing session lifecycle applies unchanged: rotation works and the old refresh token is single-use.
        var raw = new RawAuth(factory);
        var (refreshed, newRefresh, _) = await raw.RefreshAsync(auth.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotNull(newRefresh);

        // A passwordless account cannot be used with the password endpoints (no dummy password exists).
        var (login, _, _) = await raw.LoginAsync(email, "");
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);
        var (login2, _, _) = await raw.LoginAsync(email, ApiClient.Password);
        await AssertProblemAsync(login2, HttpStatusCode.Unauthorized, "AUTH_INVALID_CREDENTIALS");
        Assert.Equal("", await QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.PasswordHash).SingleAsync()));
    }

    [Fact]
    public async Task Linked_identity_is_resolved_by_subject_even_when_the_google_email_changes()
    {
        var sub = Sub();
        var email = Gmail();
        var first = (await (await GoogleSignInAsync(sub, email)).Content.ReadFromJsonAsync<AuthDto>())!;

        var second = await GoogleSignInAsync(sub, Gmail());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var auth = (await second.Content.ReadFromJsonAsync<AuthDto>())!;
        Assert.Equal(first.User.Id, auth.User.Id);
        Assert.Equal(email, auth.User.Email); // the stored email is never changed by Google
        Assert.Equal(1, await QueryAsync(db => db.UserExternalIdentities.CountAsync(i => i.ProviderSubject == sub)));
    }

    [Fact]
    public async Task Existing_password_account_with_the_same_email_is_never_linked_or_signed_in_automatically()
    {
        var owner = await ApiClient.RegisterAsync(factory, $"pw-{Guid.NewGuid():N}@gmail.com");

        var response = await GoogleSignInAsync(Sub(), owner.Email);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "AUTH_GOOGLE_LINK_REQUIRED");
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await QueryAsync(db => db.Users.CountAsync(u => u.Email == owner.Email)));
        Assert.False(await QueryAsync(db => db.UserExternalIdentities.AnyAsync(i => i.UserId == owner.UserId)));
        Assert.Equal(HttpStatusCode.OK, (await new RawAuth(factory).LoginAsync(owner.Email, ApiClient.Password)).Response.StatusCode); // password login unaffected
    }

    [Fact]
    public async Task Non_google_hosted_email_must_be_confirmed_by_opsflow_before_any_session_is_issued()
    {
        var sub = Sub();
        var email = $"outside-{Guid.NewGuid():N}@example.com";

        var response = await GoogleSignInAsync(sub, email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var mail = factory.Emails.Last(email, "/auth/confirm-email");
        await AssertProblemAsync(await GoogleSignInAsync(sub, email), HttpStatusCode.Forbidden, "AUTH_EMAIL_NOT_VERIFIED");

        var (userId, token) = ApiClient.ParseLink(mail.Body);
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await GoogleSignInAsync(sub, email)).StatusCode);
    }

    [Fact]
    public async Task Google_workspace_email_matching_the_hosted_domain_is_trusted()
    {
        var domain = $"corp{Guid.NewGuid():N}.example";
        Assert.Equal(HttpStatusCode.OK, (await GoogleSignInAsync(Sub(), $"ann@{domain}", hostedDomain: domain)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await GoogleSignInAsync(Sub(), $"bob@other-{domain}", hostedDomain: domain)).StatusCode); // hd must match the email domain
    }

    // ---------------------------------------------------------------- token and challenge validation

    [Fact]
    public async Task Invalid_credentials_unverified_email_and_missing_subject_are_rejected_without_creating_users()
    {
        var http = factory.CreateClient();
        var challenge = await ChallengeAsync(http);
        // Wrong signature / issuer / audience / expired tokens are rejected by the validator and surface as this code.
        await AssertProblemAsync(await PostGoogleAsync(http, "forged.or.expired.token", challenge.ChallengeId), HttpStatusCode.Unauthorized, "AUTH_GOOGLE_TOKEN_INVALID");

        var unverified = Gmail();
        await AssertProblemAsync(await GoogleSignInAsync(Sub(), unverified, verified: false), HttpStatusCode.Forbidden, "AUTH_GOOGLE_EMAIL_NOT_VERIFIED");
        await AssertProblemAsync(await GoogleSignInAsync("", Gmail()), HttpStatusCode.Unauthorized, "AUTH_GOOGLE_TOKEN_INVALID");
        await AssertProblemAsync(await GoogleSignInAsync(new string('s', 256), Gmail()), HttpStatusCode.Unauthorized, "AUTH_GOOGLE_TOKEN_INVALID");
        Assert.False(await QueryAsync(db => db.Users.AnyAsync(u => u.Email == unverified)));
    }

    [Fact]
    public async Task Nonce_must_match_the_challenge_and_each_challenge_works_exactly_once()
    {
        var http = factory.CreateClient();
        var email = Gmail();

        var a = await ChallengeAsync(http);
        var noNonce = factory.Google.Issue(new GoogleIdentity(Sub(), email, true, null, null, null));
        await AssertProblemAsync(await PostGoogleAsync(http, noNonce, a.ChallengeId), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");
        await AssertProblemAsync(await PostGoogleAsync(http, noNonce, a.ChallengeId), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID"); // consumed even by the failed attempt

        var b = await ChallengeAsync(http);
        var c = await ChallengeAsync(http);
        var wrongNonce = factory.Google.Issue(new GoogleIdentity(Sub(), email, true, null, c.Nonce, null));
        await AssertProblemAsync(await PostGoogleAsync(http, wrongNonce, b.ChallengeId), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");

        await AssertProblemAsync(await PostGoogleAsync(http, wrongNonce, "unknown-challenge"), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");

        Assert.Equal(HttpStatusCode.OK, (await PostGoogleAsync(http, wrongNonce, c.ChallengeId)).StatusCode);
        await AssertProblemAsync(await PostGoogleAsync(http, wrongNonce, c.ChallengeId), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID"); // replay
    }

    [Fact]
    public async Task Expired_challenges_are_rejected_before_google_is_contacted()
    {
        var http = factory.CreateClient();
        var challenge = await ChallengeAsync(http);
        Assert.Equal(ApiFactory.GoogleTestClientId, challenge.ClientId);
        Assert.InRange(challenge.ExpiresAtUtc - factory.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        var credential = factory.Google.Issue(new GoogleIdentity(Sub(), Gmail(), true, null, challenge.Nonce, null));
        var callsBefore = factory.Google.Calls;

        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        await AssertProblemAsync(await PostGoogleAsync(http, credential, challenge.ChallengeId), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");
        Assert.Equal(callsBefore, factory.Google.Calls);
    }

    [Fact]
    public async Task Google_outage_is_a_503_not_an_invalid_credential()
    {
        var http = factory.CreateClient();
        var challenge = await ChallengeAsync(http);
        var credential = factory.Google.Issue(new GoogleIdentity(Sub(), Gmail(), true, null, challenge.Nonce, null));
        factory.Google.Unavailable = true;
        try
        {
            await AssertProblemAsync(await PostGoogleAsync(http, credential, challenge.ChallengeId), HttpStatusCode.ServiceUnavailable, "AUTH_GOOGLE_UNAVAILABLE");
        }
        finally
        {
            factory.Google.Unavailable = false;
        }
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_of_one_google_account_create_exactly_one_user()
    {
        var sub = Sub();
        var email = Gmail();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GoogleSignInAsync(sub, email)));

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Equal(1, await QueryAsync(db => db.Users.CountAsync(u => u.Email == email)));
        Assert.Equal(1, await QueryAsync(db => db.UserExternalIdentities.CountAsync(i => i.ProviderSubject == sub)));
    }

    // ---------------------------------------------------------------- account policy

    [Fact]
    public async Task Deactivated_account_receives_no_session_through_google()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var sub = Sub();
        var http = Bearer(user.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, sub, Gmail()))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/v1/auth/deactivate", new { password = ApiClient.Password })).StatusCode);

        await AssertProblemAsync(await GoogleSignInAsync(sub, Gmail()), HttpStatusCode.Forbidden, "AUTH_ACCOUNT_DISABLED");
        Assert.False(await QueryAsync(db => db.UserSessions.AnyAsync(s => s.UserId == user.UserId && s.RevokedAtUtc == null)));
    }

    // ---------------------------------------------------------------- linking

    [Fact]
    public async Task Signed_in_password_user_can_link_google_and_then_sign_in_with_it()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var http = Bearer(user.AccessToken);
        var sub = Sub();

        Assert.Equal(HttpStatusCode.NoContent, (await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, sub, Gmail()))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, sub, Gmail()))).StatusCode); // idempotent

        var profile = (await http.GetFromJsonAsync<Profile>("/api/v1/auth/me"))!;
        Assert.True(profile.HasPassword && profile.GoogleLinked);
        var signIn = await GoogleSignInAsync(sub, Gmail());
        Assert.Equal(user.UserId, (await signIn.Content.ReadFromJsonAsync<AuthDto>())!.User.Id);
        Assert.Equal(HttpStatusCode.OK, (await new RawAuth(factory).LoginAsync(user.Email, ApiClient.Password)).Response.StatusCode);
    }

    [Fact]
    public async Task Linking_requires_authentication_the_correct_password_and_a_link_challenge_of_the_same_user()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/google/link/challenge", new { password = ApiClient.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/google/link", new { credential = "x", challengeId = "y" })).StatusCode);

        var user = await ApiClient.RegisterAsync(factory);
        var http = Bearer(user.AccessToken);
        var wrong = await http.PostAsJsonAsync("/api/v1/auth/google/link/challenge", new { password = "Wrong!Passw0rd#9" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // A sign-in challenge cannot authorize linking.
        var signInChallenge = await ChallengeAsync(http);
        var sub = Sub();
        var credential = factory.Google.Issue(new GoogleIdentity(sub, Gmail(), true, null, signInChallenge.Nonce, null));
        await AssertProblemAsync(await http.PostAsJsonAsync("/api/v1/auth/google/link", new { credential, challengeId = signInChallenge.ChallengeId }), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");

        // A link challenge issued to another user cannot be used.
        var other = await ApiClient.RegisterAsync(factory);
        var stolenBody = await LinkChallengeAndCredentialAsync(Bearer(other.AccessToken), sub, Gmail());
        await AssertProblemAsync(await PostLinkAsync(http, stolenBody), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");

        // A link challenge cannot be used to sign in.
        var linkBody = JsonSerializer.Deserialize<JsonElement>(await LinkChallengeAndCredentialAsync(http, sub, Gmail()));
        await AssertProblemAsync(await PostGoogleAsync(anonymous, linkBody.GetProperty("credential").GetString()!, linkBody.GetProperty("challengeId").GetString()!), HttpStatusCode.BadRequest, "AUTH_GOOGLE_CHALLENGE_INVALID");
        Assert.False(await QueryAsync(db => db.UserExternalIdentities.AnyAsync(i => i.ProviderSubject == sub)));
    }

    [Fact]
    public async Task Google_identity_of_another_user_cannot_be_taken_and_a_second_google_account_cannot_be_added()
    {
        var sub = Sub();
        var victim = (await (await GoogleSignInAsync(sub, Gmail())).Content.ReadFromJsonAsync<AuthDto>())!;
        var attacker = await ApiClient.RegisterAsync(factory);
        var http = Bearer(attacker.AccessToken);

        await AssertProblemAsync(await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, sub, Gmail())), HttpStatusCode.Conflict, "AUTH_GOOGLE_IDENTITY_CONFLICT");
        Assert.Equal(victim.User.Id, await QueryAsync(db => db.UserExternalIdentities.Where(i => i.ProviderSubject == sub).Select(i => i.UserId).SingleAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, Sub(), Gmail()))).StatusCode);
        await AssertProblemAsync(await PostLinkAsync(http, await LinkChallengeAndCredentialAsync(http, Sub(), Gmail())), HttpStatusCode.Conflict, "AUTH_GOOGLE_ALREADY_LINKED");
    }

    [Fact]
    public async Task Concurrent_links_of_one_google_account_by_two_users_leave_exactly_one_owner()
    {
        var sub = Sub();
        var a = await ApiClient.RegisterAsync(factory);
        var b = await ApiClient.RegisterAsync(factory);
        var httpA = Bearer(a.AccessToken);
        var httpB = Bearer(b.AccessToken);
        var bodyA = await LinkChallengeAndCredentialAsync(httpA, sub, Gmail());
        var bodyB = await LinkChallengeAndCredentialAsync(httpB, sub, Gmail());

        var results = await Task.WhenAll(PostLinkAsync(httpA, bodyA), PostLinkAsync(httpB, bodyB));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await QueryAsync(db => db.UserExternalIdentities.CountAsync(i => i.ProviderSubject == sub)));
    }

    [Fact]
    public async Task Google_only_account_cannot_start_linking_or_use_password_endpoints()
    {
        var auth = (await (await GoogleSignInAsync(Sub(), Gmail())).Content.ReadFromJsonAsync<AuthDto>())!;
        var http = Bearer(auth.AccessToken);

        await AssertProblemAsync(await http.PostAsJsonAsync("/api/v1/auth/google/link/challenge", new { password = ApiClient.Password }), HttpStatusCode.Forbidden, "AUTH_PASSWORD_NOT_SET");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "", newPassword = "N3w!Passw0rd#2" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/auth/deactivate", new { password = "anything" })).StatusCode);
    }

    // ---------------------------------------------------------------- profile

    [Fact]
    public async Task User_updates_only_their_own_display_name()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var other = await ApiClient.RegisterAsync(factory);

        var response = await user.PutAsync("/api/v1/auth/me", new { displayName = "  Ada Lovelace  ", email = "evil@example.com", id = other.UserId, hasPassword = false });

        var profile = await user.ReadAsync<Profile>(response);
        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal(user.Email, profile.Email);
        Assert.Equal(user.UserId, profile.Id);
        Assert.Equal("Ada Lovelace", (await user.ReadAsync<Profile>(await user.GetAsync("/api/v1/auth/me"))).DisplayName);
        Assert.Equal("Test User", (await other.ReadAsync<Profile>(await other.GetAsync("/api/v1/auth/me"))).DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await new RawAuth(factory).LoginAsync(user.Email, ApiClient.Password)).Response.StatusCode); // password untouched
        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode); // no token reissue needed, session intact
    }

    [Theory]
    [InlineData("")]
    [InlineData(" a ")]
    [InlineData("Bad\u0000Name")]
    [InlineData("Zero​Width")]
    public async Task Invalid_display_names_are_rejected(string displayName)
    {
        var user = await ApiClient.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsync("/api/v1/auth/me", new { displayName })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsync("/api/v1/auth/me", new { displayName = new string('x', 101) })).StatusCode);
        Assert.Equal("Test User", (await user.ReadAsync<Profile>(await user.GetAsync("/api/v1/auth/me"))).DisplayName);
    }

    [Fact]
    public async Task Anonymous_profile_update_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync("/api/v1/auth/me", new { displayName = "Nobody" })).StatusCode);
    }
}

public class GoogleDisabledTests(GoogleDisabledApiFactory factory) : IClassFixture<GoogleDisabledApiFactory>
{
    [Fact]
    public async Task Without_a_client_id_google_endpoints_report_disabled_and_password_login_still_works()
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/google/challenge", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("AUTH_GOOGLE_DISABLED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/auth/google", new { credential = "x", challengeId = "y" })).StatusCode);

        var user = await ApiClient.RegisterAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode);
    }
}
