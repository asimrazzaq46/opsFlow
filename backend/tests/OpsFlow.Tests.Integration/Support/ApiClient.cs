using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;

namespace OpsFlow.Tests.Integration.Support;

public sealed record UserDto(Guid Id, string Email, string DisplayName);
public sealed record AuthDto(string AccessToken, string TokenType, DateTime ExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc, UserDto User);
public sealed record SessionDto(Guid Id, string? UserAgent, DateTime CreatedAtUtc, DateTime LastUsedAtUtc, bool IsCurrent);
public sealed record OrgDto(Guid Id, string Name, string Slug, string Role);
public sealed record MemberDto(Guid Id, Guid UserId, string Email, string DisplayName, string Role, bool IsActive);
public sealed record PagedDto<T>(List<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Bearer-only client: keeps access and refresh token in memory exactly like the Angular client is meant to (no cookies).</summary>
public sealed class ApiClient(HttpClient http)
{
    public const string Password = "Str0ng!Passw0rd#1";

    public string Email { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public string AccessToken { get; private set; } = string.Empty;
    public string RefreshToken { get; private set; } = string.Empty;

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => http.PostAsJsonAsync(url, body ?? new { });

    public Task<HttpResponseMessage> PutAsync(string url, object body) => http.PutAsJsonAsync(url, body);

    public Task<HttpResponseMessage> DeleteAsync(string url) => http.DeleteAsync(url);

    public async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var response = await PostAsync("/api/v1/auth/login", new { email, password });
        if (response.IsSuccessStatusCode)
        {
            Email = email;
            await UseAsync(response);
        }

        return response;
    }

    /// <summary>POST /auth/refresh with the refresh token held in memory; adopts the rotated tokens on success.</summary>
    public async Task<HttpResponseMessage> RefreshAsync()
    {
        var response = await PostAsync("/api/v1/auth/refresh", new { refreshToken = RefreshToken });
        if (response.IsSuccessStatusCode)
        {
            await UseAsync(response);
        }

        return response;
    }

    /// <summary>POST /auth/change-password; adopts the fresh session the API returns.</summary>
    public async Task<HttpResponseMessage> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var response = await PostAsync("/api/v1/auth/change-password", new { currentPassword, newPassword });
        if (response.IsSuccessStatusCode)
        {
            await UseAsync(response);
        }

        return response;
    }

    private async Task UseAsync(HttpResponseMessage authResponse)
    {
        var auth = (await authResponse.Content.ReadFromJsonAsync<AuthDto>())!;
        AccessToken = auth.AccessToken;
        RefreshToken = auth.RefreshToken;
        UserId = auth.User.Id;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    }

    public async Task<OrgDto> CreateOrganizationAsync(string name = "Acme Support") =>
        await ReadAsync<OrgDto>(await PostAsync("/api/v1/organizations", new { name }));

    public async Task<List<MemberDto>> MembersAsync(Guid tenantId) =>
        (await ReadAsync<PagedDto<MemberDto>>(await GetAsync($"/api/v1/tenants/{tenantId}/members"))).Items;

    public static (string UserId, string Token) ParseLink(string body)
    {
        var link = body.Split(' ', '\n').First(p => p.StartsWith("http", StringComparison.Ordinal));
        var query = HttpUtility.ParseQueryString(new Uri(link).Query);
        return (query["userId"] ?? string.Empty, query["token"] ?? string.Empty);
    }

    /// <summary>Registers, verifies the email from the captured mail, and signs in.</summary>
    public static async Task<ApiClient> RegisterAsync(ApiFactory factory, string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@example.com";
        var client = factory.CreateApiClient();
        (await client.PostAsync("/api/v1/auth/register", new { email, password = Password, displayName = "Test User" })).EnsureSuccessStatusCode();

        var (userId, token) = ParseLink(factory.Emails.Last(email, "/auth/confirm-email").Body);
        (await client.PostAsync("/api/v1/auth/confirm-email", new { userId, token })).EnsureSuccessStatusCode();
        (await client.LoginAsync(email, Password)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Invites a new user by email as <paramref name="role"/> and has them accept; returns their signed-in client.</summary>
    public static async Task<ApiClient> AddMemberAsync(ApiFactory factory, ApiClient inviter, Guid tenantId, string role)
    {
        var email = $"member-{Guid.NewGuid():N}@example.com";
        (await inviter.PostAsync($"/api/v1/tenants/{tenantId}/invitations", new { email, role })).EnsureSuccessStatusCode();
        var member = await RegisterAsync(factory, email);
        var token = InvitationTokenFrom(factory, email);
        (await member.PostAsync("/api/v1/invitations/accept", new { token })).EnsureSuccessStatusCode();
        return member;
    }

    public static string InvitationTokenFrom(ApiFactory factory, string email)
    {
        var mail = factory.Emails.Last(email, "/invitations/accept");
        return HttpUtility.ParseQueryString(new Uri(mail.Body.Split(' ', '\n').First(p => p.Contains("/invitations/accept", StringComparison.Ordinal))).Query)["token"]!;
    }
}

/// <summary>Stateless client for tests that must replay a specific (old) refresh token or race several requests with the same token.</summary>
public sealed class RawAuth(ApiFactory factory)
{
    private readonly HttpClient _http = factory.CreateClient();

    public async Task<(HttpResponseMessage Response, string? RefreshToken, string? AccessToken)> LoginAsync(string email, string password) =>
        await Parse(await _http.PostAsJsonAsync("/api/v1/auth/login", new { email, password }));

    public async Task<(HttpResponseMessage Response, string? RefreshToken, string? AccessToken)> RefreshAsync(string refreshToken) =>
        await Parse(await _http.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken }));

    public Task<HttpResponseMessage> LogoutAsync(string refreshToken) => _http.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken });

    public async Task<HttpResponseMessage> GetAsync(string url, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostBearerAsync(string url, string accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request);
    }

    private static async Task<(HttpResponseMessage Response, string? RefreshToken, string? AccessToken)> Parse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return (response, null, null);
        }

        var auth = (await response.Content.ReadFromJsonAsync<AuthDto>())!;
        return (response, auth.RefreshToken, auth.AccessToken);
    }
}
