using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

/// <summary>Uniform RFC 9457 error bodies, success status semantics, JWT refresh-token separation and the OpenAPI/Bearer contract.</summary>
public class ApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string SigningKey = "integration-test-signing-key-0123456789-abcdefghij";

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("about:blank", json.GetProperty("type").GetString());
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
        foreach (var leak in new[] { "Npgsql", "SQLite", "StackTrace", "   at ", "OpsFlow.Application", "injected", "eyJ" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }

        return json;
    }

    private static HttpRequestMessage Get(string url, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    // ---------------------------------------------------------------- error mapping

    [Fact]
    public async Task Invalid_login_is_a_401_problem_with_a_stable_code()
    {
        var response = await factory.CreateApiClient().PostAsync("/api/v1/auth/login", new { email = "nobody@example.com", password = "Wrong!Passw0rd#1" });

        var json = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "AUTH_INVALID_CREDENTIALS");
        Assert.Equal("Invalid credentials or the account is not available.", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Missing_invalid_and_expired_bearer_tokens_get_distinct_401_codes()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var http = factory.CreateClient();
        var truncated = user.AccessToken[..^3] + "abc"; // signature no longer matches

        await AssertProblemAsync(await http.SendAsync(Get("/api/v1/organizations", null)), HttpStatusCode.Unauthorized, "AUTH_TOKEN_MISSING");
        await AssertProblemAsync(await http.SendAsync(Get("/api/v1/organizations", truncated)), HttpStatusCode.Unauthorized, "AUTH_TOKEN_INVALID");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Get("/api/v1/organizations", user.AccessToken))).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(20)); // access tokens live 15 minutes
        var expired = await http.SendAsync(Get("/api/v1/organizations", user.AccessToken));

        await AssertProblemAsync(expired, HttpStatusCode.Unauthorized, "AUTH_TOKEN_EXPIRED");
        Assert.Contains("Bearer", expired.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_and_malformed_requests_are_400_problems()
    {
        var anonymous = factory.CreateApiClient();

        var invalid = await anonymous.PostAsync("/api/v1/auth/register", new { email = "not-an-email", password = ApiClient.Password, displayName = "X" });
        var json = await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        Assert.True(json.GetProperty("errors").EnumerateObject().Any());

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json") };
        await AssertProblemAsync(await factory.CreateClient().SendAsync(malformed), HttpStatusCode.BadRequest, "VALIDATION_ERROR"); // MVC: JSON-parse errors are model-state errors
    }

    [Fact]
    public async Task Forbidden_missing_and_conflicting_resources_use_the_same_problem_shape()
    {
        var w = await TicketWorld.CreateAsync(factory);

        await AssertProblemAsync(await w.Customer.GetAsync(w.Url("members")), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await w.Manager.GetAsync(w.Url($"tickets/{Guid.NewGuid()}")), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await w.Manager.PostAsync(w.Url("ticket-categories"), new { name = "General" }), HttpStatusCode.Conflict, "CONFLICT");
    }

    [Fact]
    public async Task Unexpected_failures_are_a_generic_500_without_leaking_details()
    {
        var w = await TicketWorld.CreateAsync(factory);
        factory.Faults.FailNextSave();

        var response = await w.Manager.PostAsync(w.Url("ticket-categories"), new { name = "Boom" });

        var json = await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.Equal("An unexpected error occurred. Please try again later.", json.GetProperty("detail").GetString());
    }

    // ---------------------------------------------------------------- success semantics

    [Fact]
    public async Task Success_statuses_follow_http_semantics()
    {
        var anonymous = factory.CreateApiClient();
        var register = await anonymous.PostAsync("/api/v1/auth/register", new { email = $"s-{Guid.NewGuid():N}@example.com", password = ApiClient.Password, displayName = "Success" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode); // enumeration-safe
        var w = await TicketWorld.CreateAsync(factory);

        var org = await w.Owner.PostAsync("/api/v1/organizations", new { name = $"Created {Guid.NewGuid():N}" });
        var ticket = await w.Manager.PostAsync(w.Url("tickets"), new { title = "Created", description = "d", categoryId = w.CategoryId });
        var list = await w.Manager.GetAsync(w.Url("tickets"));
        var role = await w.Owner.PutAsync(w.Url($"members/{w.Agent1Membership}/role"), new { role = "Agent" });

        Assert.Equal(HttpStatusCode.Created, org.StatusCode);
        Assert.StartsWith("/api/v1/tenants/", org.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, ticket.StatusCode);
        Assert.Contains("/tickets/", ticket.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
    }

    // ---------------------------------------------------------------- JWT access / refresh separation

    private static string SignedToken(IEnumerable<Claim> claims, string issuer = "opsflow-api", string audience = "opsflow-app", string algorithm = SecurityAlgorithms.HmacSha256, string key = SigningKey) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key.PadRight(64, 'x'))), algorithm),
        });

    [Fact]
    public async Task Refresh_tokens_are_jwts_with_a_distinct_type_and_minimal_claims()
    {
        var user = await ApiClient.RegisterAsync(factory);

        var refresh = new JsonWebTokenHandler().ReadJsonWebToken(user.RefreshToken);
        var access = new JsonWebTokenHandler().ReadJsonWebToken(user.AccessToken);

        Assert.Equal("refresh", refresh.GetClaim("token_type").Value);
        Assert.Equal("access", access.GetClaim("token_type").Value);
        Assert.Subset(new HashSet<string> { "sub", "sid", "jti", "token_type", "iat", "nbf", "exp", "iss", "aud" }, refresh.Claims.Select(c => c.Type).ToHashSet());
        Assert.InRange((refresh.ValidTo - refresh.ValidFrom).TotalDays, 6.99, 7.01); // configurable 7-day lifetime
        Assert.Equal(access.GetClaim("sid").Value, refresh.GetClaim("sid").Value);
    }

    [Fact]
    public async Task Only_the_hash_of_a_refresh_jwt_is_stored()
    {
        var user = await ApiClient.RegisterAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();

        Assert.False(await db.RefreshTokens.AnyAsync(t => t.TokenHash == user.RefreshToken));
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.TokenHash == SecureToken.Hash(user.RefreshToken)));
    }

    [Fact]
    public async Task A_refresh_jwt_cannot_authenticate_api_requests()
    {
        var user = await ApiClient.RegisterAsync(factory);

        var response = await factory.CreateClient().SendAsync(Get("/api/v1/auth/me", user.RefreshToken));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "AUTH_TOKEN_INVALID");
    }

    [Fact]
    public async Task An_access_jwt_cannot_be_used_to_refresh_and_does_not_revoke_the_session()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var http = factory.CreateClient();

        var response = await http.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = user.AccessToken });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "AUTH_INVALID_REFRESH_TOKEN");
        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode); // the real refresh token is unaffected
    }

    [Fact]
    public async Task Forged_or_untyped_or_foreign_refresh_jwts_are_rejected()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var http = factory.CreateClient();
        var sid = new JsonWebTokenHandler().ReadJsonWebToken(user.RefreshToken).GetClaim("sid").Value;

        Claim[] Claims(string type) => [new("sub", user.UserId.ToString()), new("sid", sid), new("jti", Guid.NewGuid().ToString("N")), new("token_type", type)];
        var candidates = new Dictionary<string, string>
        {
            ["valid signature + type but never issued by the server"] = SignedToken(Claims("refresh")),
            ["wrong key"] = SignedToken(Claims("refresh"), key: "a-completely-different-signing-key-0123456789"),
            ["wrong issuer"] = SignedToken(Claims("refresh"), issuer: "evil"),
            ["wrong audience"] = SignedToken(Claims("refresh"), audience: "evil"),
            ["wrong algorithm"] = SignedToken(Claims("refresh"), algorithm: SecurityAlgorithms.HmacSha512),
            ["no token type"] = SignedToken(Claims("refresh").Where(c => c.Type != "token_type")),
            ["access type"] = SignedToken(Claims("access")),
            ["not a jwt"] = "this-is-not-a-token",
        };

        foreach (var (name, token) in candidates)
        {
            var response = await http.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name} was not rejected ({(int)response.StatusCode})");
        }

        Assert.Equal(HttpStatusCode.OK, (await user.RefreshAsync()).StatusCode); // none of the attempts touched the session
    }

    [Fact]
    public async Task Access_jwts_without_the_access_type_are_rejected_even_when_correctly_signed()
    {
        var user = await ApiClient.RegisterAsync(factory);
        var sid = new JsonWebTokenHandler().ReadJsonWebToken(user.AccessToken).GetClaim("sid").Value;
        var untyped = SignedToken([new("sub", user.UserId.ToString()), new("sid", sid), new("av", "1")]);

        await AssertProblemAsync(await factory.CreateClient().SendAsync(Get("/api/v1/auth/me", untyped)), HttpStatusCode.Unauthorized, "AUTH_TOKEN_INVALID");
    }

    // ---------------------------------------------------------------- MVC routing + explicit authorization

    [Fact]
    public void Every_api_endpoint_is_an_mvc_controller_action_with_explicit_authorization_and_a_unique_route()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/", StringComparison.Ordinal)).ToList();
        string[] anonymous = ["GetHealth", "Register", "ConfirmEmail", "ResendConfirmation", "Login", "RefreshSession", "Logout", "ForgotPassword", "ResetPassword"];

        Assert.Equal(64, endpoints.Count); // UPDATED: 50 purane + 11 Milestone 3 (messages 2, notes 2, attachments 3, notifications 4) + 3 Milestone 5 (ExportReportCsv, GetTenantSettings, UpdateTenantSettings; GraphQL alag endpoint hai)
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<ControllerActionDescriptor>())); // no Minimal API mapping left
        var routes = endpoints.Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText!.TrimEnd('/')}").ToList();
        Assert.Equal(routes.Count, routes.Distinct().Count()); // no ambiguous routes

        foreach (var endpoint in endpoints)
        {
            var id = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName;
            var isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.Equal(anonymous.Contains(id), isAnonymous); // public stays public, protected never becomes anonymous
            if (!isAnonymous)
            {
                Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()); // protected by an explicit [Authorize], not only the fallback policy
            }
        }
    }

    [Fact]
    public async Task Every_protected_action_rejects_anonymous_callers_and_public_ones_do_not()
    {
        var http = factory.CreateClient();
        var id = Guid.NewGuid();
        var tenant = $"/api/v1/tenants/{id}";
        string[] protectedGets = ["/api/v1/auth/me", "/api/v1/auth/sessions", "/api/v1/organizations", tenant, tenant + "/members", tenant + "/customers", tenant + "/tickets", tenant + "/ticket-categories", tenant + "/sla-policies", tenant + "/invitations"];

        foreach (var url in protectedGets)
        {
            await AssertProblemAsync(await http.GetAsync(url), HttpStatusCode.Unauthorized, "AUTH_TOKEN_MISSING");
        }

        await AssertProblemAsync(await http.PostAsJsonAsync("/api/v1/invitations/accept", new { token = "x" }), HttpStatusCode.Unauthorized, "AUTH_TOKEN_MISSING");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "a@example.com", password = "x" })).StatusCode); // reachable, answers on its own merits
    }

    // ---------------------------------------------------------------- OpenAPI / Bearer contract

    private async Task<JsonElement> OpenApiAsync()
    {
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task The_openapi_document_declares_a_bearer_scheme_and_secures_only_protected_operations()
    {
        var doc = await OpenApiAsync();
        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());

        string[] anonymous =
        [
            "GetHealth", "Register", "ConfirmEmail", "ResendConfirmation", "Login", "RefreshSession", "Logout", "ForgotPassword", "ResetPassword",
        ];
        var seen = new List<string>();
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var id = operation.Value.GetProperty("operationId").GetString()!;
                seen.Add(id);
                var secured = operation.Value.TryGetProperty("security", out var security) && security.GetArrayLength() == 1 && security[0].TryGetProperty("Bearer", out _);
                var responses = operation.Value.GetProperty("responses");

                Assert.Equal(!anonymous.Contains(id), secured);
                if (secured)
                {
                    Assert.True(responses.TryGetProperty("401", out _), $"{id} must document 401");
                    if (path.Name.StartsWith("/api/v1/tenants/", StringComparison.Ordinal))
                    {
                        Assert.True(responses.TryGetProperty("403", out _), $"{id} must document 403");
                    }
                }

                Assert.False(string.IsNullOrWhiteSpace(operation.Value.GetProperty("summary").GetString()), $"{id} needs a summary");
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.GetProperty("description").GetString()), $"{id} needs a description");
            }
        }

        Assert.Equal(anonymous.Length, anonymous.Count(seen.Contains)); // every anonymous operation really exists
        Assert.Equal(seen.Count, seen.Distinct().Count()); // operation ids stay unique and stable
    }

    [Fact]
    public async Task Documented_errors_share_the_problem_schemas_and_login_documents_its_401()
    {
        var doc = await OpenApiAsync();
        var schemas = doc.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("ProblemResponse", out var problem));
        Assert.True(schemas.TryGetProperty("ValidationProblemResponse", out _));
        Assert.True(problem.GetProperty("properties").TryGetProperty("code", out _));

        var login = doc.GetProperty("paths").GetProperty("/api/v1/auth/login").GetProperty("post");
        Assert.Equal("Authenticate user", login.GetProperty("summary").GetString());
        Assert.True(login.GetProperty("responses").TryGetProperty("401", out var unauthorized));
        Assert.Contains("ProblemResponse", unauthorized.GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString(), StringComparison.Ordinal);
        Assert.False(login.TryGetProperty("security", out _));

        var organizations = doc.GetProperty("paths").GetProperty("/api/v1/organizations").GetProperty("get");
        Assert.Equal("List user organizations", organizations.GetProperty("summary").GetString());
    }
}

public class ProblemRateLimitTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task Rate_limited_requests_are_a_429_problem()
    {
        var client = factory.CreateApiClient();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 5 && limited is null; i++)
        {
            var response = await client.PostAsync("/api/v1/auth/login", new { email = "x@example.com", password = "nope" });
            limited = response.StatusCode == HttpStatusCode.TooManyRequests ? response : null;
        }

        Assert.NotNull(limited);
        var json = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RATE_LIMITED", json.GetProperty("code").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
    }
}
