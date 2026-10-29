using System.Net.Http.Json;
using System.Web;

namespace OpsFlow.Tests.Integration.Support;

public sealed record UserDto(Guid Id, string Email, string DisplayName);
public sealed record OrgDto(Guid Id, string Name, string Slug, string Role);
public sealed record MemberDto(Guid Id, Guid UserId, string Email, string DisplayName, string Role, bool IsActive);
public sealed record PagedDto<T>(List<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>HttpClient with a cookie jar that echoes the CSRF token like the Angular client does.</summary>
public sealed class ApiClient(HttpClient http)
{
    public const string Password = "Str0ng!Passw0rd#1";

    public string Email { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => http.PostAsJsonAsync(url, body ?? new { });

    public Task<HttpResponseMessage> PutAsync(string url, object body) => http.PutAsJsonAsync(url, body);

    public Task<HttpResponseMessage> DeleteAsync(string url) => http.DeleteAsync(url);

    public async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task RefreshCsrfAsync()
    {
        var response = await http.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        var value = Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
        http.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        http.DefaultRequestHeaders.Add("X-XSRF-TOKEN", value);
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        await RefreshCsrfAsync();
        var response = await PostAsync("/api/v1/auth/login", new { email, password });
        if (response.IsSuccessStatusCode)
        {
            Email = email;
            UserId = (await response.Content.ReadFromJsonAsync<UserDto>())!.Id;
            await RefreshCsrfAsync(); // the antiforgery token is bound to the signed-in identity
        }

        return response;
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
        await client.RefreshCsrfAsync();
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
