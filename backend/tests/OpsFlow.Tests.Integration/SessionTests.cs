using System.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Refresh-token rotation, replay/family revocation, concurrent refresh, logout and device sessions.</summary>
public class SessionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(RawAuth Raw, string Email, string Refresh, string Access)> LoginRawAsync()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var raw = new RawAuth(factory);
        var (response, refresh, access) = await raw.LoginAsync(user.Email, ApiClient.Password);
        response.EnsureSuccessStatusCode();
        return (raw, user.Email, refresh!, access!);
    }

    private static async Task<HttpStatusCode> MeAsync(RawAuth raw, string access) => (await raw.GetAsync("/api/v1/auth/me", access)).StatusCode;

    [Fact]
    public async Task Refresh_rotates_the_token_and_issues_a_working_access_token()
    {
        var (raw, _, refresh1, _) = await LoginRawAsync();

        var (response, refresh2, access2) = await raw.RefreshAsync(refresh1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(refresh2);
        Assert.NotEqual(refresh1, refresh2);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(raw, access2!));
    }

    [Fact]
    public async Task Access_token_carries_identifiers_only_never_roles_or_tenants()
    {
        var (_, _, _, access) = await LoginRawAsync();

        var claims = new JsonWebTokenHandler().ReadJsonWebToken(access).Claims.Select(c => c.Type).ToHashSet();

        Assert.Subset(new HashSet<string> { "sub", "sid", "av", "jti", "iat", "nbf", "exp", "iss", "aud" }, claims);
        Assert.Contains("sid", claims);
    }

    [Fact]
    public async Task Replaying_a_used_refresh_token_later_revokes_the_whole_session()
    {
        var (raw, _, refresh1, _) = await LoginRawAsync();
        var (_, refresh2, access2) = await raw.RefreshAsync(refresh1);
        factory.Clock.Advance(TimeSpan.FromSeconds(11)); // beyond the 10 s grace window

        var replay = await raw.RefreshAsync(refresh1);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(refresh2!)).Response.StatusCode); // the descendant token died too
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(raw, access2!)); // and so did its access token
    }

    [Fact]
    public async Task Replaying_within_the_grace_window_is_rejected_without_revoking_the_session()
    {
        var (raw, _, refresh1, _) = await LoginRawAsync();
        var (_, refresh2, _) = await raw.RefreshAsync(refresh1);

        var racing = await raw.RefreshAsync(refresh1); // e.g. a second tab that lost the race

        Assert.Equal(HttpStatusCode.Conflict, racing.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await raw.RefreshAsync(refresh2!)).Response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_refresh_with_the_same_token_lets_exactly_one_request_win()
    {
        var (raw, _, refresh1, _) = await LoginRawAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => raw.RefreshAsync(refresh1)));

        var winners = results.Where(r => r.Response.StatusCode == HttpStatusCode.OK).ToList();
        Assert.Single(winners);
        Assert.All(results.Where(r => r.Response.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.Response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.Unauthorized }));
        var winner = winners.Single();
        Assert.Equal(HttpStatusCode.OK, await MeAsync(raw, winner.AccessToken!));
        Assert.Equal((2, 1, false), await TokenStateAsync(refresh1)); // exactly ONE valid successor, never two

        // later reuse of the original token is treated as theft: the winner's token family is revoked as well
        factory.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(refresh1)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(winner.RefreshToken!)).Response.StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_expire_and_sessions_have_an_absolute_lifetime()
    {
        var (raw, _, refresh, _) = await LoginRawAsync();
        factory.Clock.Advance(TimeSpan.FromDays(8)); // refresh lifetime is 7 days
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(refresh)).Response.StatusCode);

        var (raw2, _, current, _) = await LoginRawAsync();
        var survived = 0;
        for (var i = 0; i < 8; i++)
        {
            factory.Clock.Advance(TimeSpan.FromDays(6)); // each rotation is fresh, but the 30-day session cap still applies
            var (response, next, _) = await raw2.RefreshAsync(current);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                break;
            }

            current = next!;
            survived++;
        }

        Assert.InRange(survived, 4, 5); // 30 days / 6 days
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_its_tokens()
    {
        var (raw, _, refresh, access) = await LoginRawAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await raw.LogoutAsync(refresh)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.RefreshAsync(refresh)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(raw, access));
    }

    [Fact]
    public async Task Logout_all_revokes_every_device()
    {
        var (rawA, email, refreshA, accessA) = await LoginRawAsync();
        var rawB = new RawAuth(factory);
        var (_, refreshB, accessB) = await rawB.LoginAsync(email, ApiClient.Password);

        Assert.Equal(HttpStatusCode.NoContent, (await rawA.PostBearerAsync("/api/v1/auth/logout-all", accessA)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await rawA.RefreshAsync(refreshA)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rawB.RefreshAsync(refreshB!)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(rawB, accessB!));
    }

    [Fact]
    public async Task Devices_are_independent_sessions_that_can_be_listed_and_revoked_one_by_one()
    {
        var phone = await ApiClient.RegisterAsync(factory);
        var laptop = factory.CreateApiClient();
        await laptop.LoginAsync(phone.Email, ApiClient.Password);

        var sessions = await phone.ReadAsync<List<SessionDto>>(await phone.GetAsync("/api/v1/auth/sessions"));
        var other = sessions.Single(s => !s.IsCurrent);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.IsCurrent);

        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync($"/api/v1/auth/sessions/{other.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/auth/me")).StatusCode); // the other device is unaffected
        Assert.Equal(HttpStatusCode.NotFound, (await phone.DeleteAsync($"/api/v1/auth/sessions/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_revoke_someone_elses_session()
    {
        var victim = await ApiClient.RegisterAsync(factory);
        var attacker = await ApiClient.RegisterAsync(factory);
        var victimSession = (await victim.ReadAsync<List<SessionDto>>(await victim.GetAsync("/api/v1/auth/sessions"))).Single();

        Assert.Equal(HttpStatusCode.NotFound, (await attacker.DeleteAsync($"/api/v1/auth/sessions/{victimSession.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    private async Task<(OpsFlowDbContext Db, SessionService Service, IServiceScope Scope)> ServiceScopeAsync()
    {
        var scope = factory.Services.CreateScope();
        await Task.CompletedTask;
        return (scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope.ServiceProvider.GetRequiredService<SessionService>(), scope);
    }

    private async Task<(int Tokens, int Unused, bool Revoked)> TokenStateAsync(string anyRefreshTokenOfTheSession)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var hash = SecureToken.Hash(anyRefreshTokenOfTheSession);
        var sessionId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => t.SessionId).SingleAsync();
        var tokens = await db.RefreshTokens.Where(t => t.SessionId == sessionId).ToListAsync();
        return (tokens.Count, tokens.Count(t => t.UsedAtUtc == null), await db.UserSessions.AnyAsync(s => s.Id == sessionId && s.RevokedAtUtc != null));
    }

    [Fact]
    public async Task A_refresh_race_lost_within_the_grace_window_issues_nothing_and_keeps_the_session()
    {
        var (_, _, refresh1, _) = await LoginRawAsync();
        var (dbB, loser, scopeB) = await ServiceScopeAsync();
        var (_, winner, scopeA) = await ServiceScopeAsync();
        using (scopeB)
        using (scopeA)
        {
            factory.SaveGate.PauseNextSave(dbB);
            var losing = Task.Run(() => loser.RefreshAsync(refresh1, null, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            var (winning, _) = await winner.RefreshAsync(refresh1, null, CancellationToken.None); // commits first
            factory.SaveGate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout));

            Assert.Equal(409, failure.StatusCode);
            var state = await TokenStateAsync(refresh1);
            Assert.Equal((2, 1, false), state); // original (used) + exactly ONE successor; session alive
            Assert.NotNull((await winner.RefreshAsync(winning.RefreshToken, null, CancellationToken.None)).Tokens); // the one successor works
        }
    }

    [Fact]
    public async Task A_refresh_race_lost_after_the_grace_window_is_theft_and_revokes_the_family()
    {
        var (_, _, refresh1, _) = await LoginRawAsync();
        var (dbB, loser, scopeB) = await ServiceScopeAsync();
        var (_, winner, scopeA) = await ServiceScopeAsync();
        using (scopeB)
        using (scopeA)
        {
            factory.SaveGate.PauseNextSave(dbB);
            var losing = Task.Run(() => loser.RefreshAsync(refresh1, null, CancellationToken.None));
            Assert.True(await factory.SaveGate.WaitUntilPausedAsync());

            var (winning, _) = await winner.RefreshAsync(refresh1, null, CancellationToken.None);
            factory.Clock.Advance(TimeSpan.FromSeconds(11)); // the stalled request resumes after the grace window
            factory.SaveGate.Resume();
            var failure = await Assert.ThrowsAsync<AppException>(() => losing.WaitAsync(QueryGate.Timeout));

            Assert.Equal(401, failure.StatusCode);
            Assert.Equal((2, 1, true), await TokenStateAsync(refresh1)); // no second successor, session revoked
            var (_, fresh, freshScope) = await ServiceScopeAsync(); // a NEW request/scope, like in production
            using var _ = freshScope;
            var successor = await Assert.ThrowsAsync<AppException>(() => fresh.RefreshAsync(winning.RefreshToken, null, CancellationToken.None));
            Assert.Equal(401, successor.StatusCode); // the only successor died with its family
        }
    }

    [Fact]
    public async Task Refresh_requires_a_token_in_the_body()
    {
        var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/refresh", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-real-token" })).StatusCode);
    }
}
