using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Security;
using OpsFlow.Realtime;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>The live-connection batch check against a real database: exactly one query, and only an exact (user, session, auth version) match is valid.</summary>
public class RealtimeSessionValidatorTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>The principal a hub connection would carry: the claims of the (already validated) access token.</summary>
    private static ClaimsPrincipal PrincipalOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return Principal(json.RootElement.GetProperty("sub").GetString()!, json.RootElement.GetProperty("sid").GetString()!, json.RootElement.GetProperty("av").GetRawText());
    }

    private static ClaimsPrincipal Principal(string sub, string? sid, string? av)
    {
        List<Claim> claims = [new("sub", sub)];
        if (sid is not null)
        {
            claims.Add(new Claim("sid", sid));
        }

        if (av is not null)
        {
            claims.Add(new Claim("av", av));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task The_batch_check_is_one_query_and_requires_the_exact_user_session_and_auth_version()
    {
        List<ApiClient> users = [];
        for (var i = 0; i < 6; i++)
        {
            users.Add(await ApiClient.RegisterAsync(factory));
        }

        var principals = users.Select(u => PrincipalOf(u.AccessToken)).ToList();
        var connections = principals.Select((p, i) => new RealtimeConnectionInfo($"ok{i}", p)).ToList();
        string Claim(int user, string type) => principals[user].FindFirst(type)!.Value;
        connections.Add(new("other-users-session", Principal(Claim(0, "sub"), Claim(1, "sid"), Claim(0, "av")))); // user 0 claiming user 1's session
        connections.Add(new("stale-auth-version", Principal(Claim(2, "sub"), Claim(2, "sid"), "999")));
        connections.Add(new("unknown-session", Principal(Claim(3, "sub"), Guid.NewGuid().ToString(), Claim(3, "av"))));
        connections.Add(new("no-session-claim", Principal(Claim(4, "sub"), null, Claim(4, "av"))));
        connections.Add(new("malformed", Principal("not-a-guid", "x", "y")));

        using var scope = factory.Services.CreateScope();
        var validator = new RealtimeSessionValidator(scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), factory.Clock);
        IReadOnlySet<string> valid = new HashSet<string>();
        var queries = await factory.Queries.MeasureAsync(async () => valid = await validator.FindValidAsync(connections, CancellationToken.None));

        Assert.Equal(1, queries); // never one query per connection
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"ok{i}").Order(StringComparer.Ordinal), valid.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_revoked_session_is_the_only_one_dropped_from_a_batch()
    {
        var keeper = await ApiClient.RegisterAsync(factory);
        var device = factory.CreateApiClient();
        (await device.LoginAsync(keeper.Email, ApiClient.Password)).EnsureSuccessStatusCode();
        var other = await ApiClient.RegisterAsync(factory);
        (await device.PostAsync("/api/v1/auth/logout", new { refreshToken = device.RefreshToken })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var validator = new RealtimeSessionValidator(scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), factory.Clock);
        var valid = await validator.FindValidAsync(
            [new("keeper", PrincipalOf(keeper.AccessToken)), new("device", PrincipalOf(device.AccessToken)), new("other", PrincipalOf(other.AccessToken))],
            CancellationToken.None);

        Assert.Equal(["keeper", "other"], valid.Order(StringComparer.Ordinal));
    }
}
