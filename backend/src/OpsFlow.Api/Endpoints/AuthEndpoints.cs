using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Domain.Users;

namespace OpsFlow.Api.Endpoints;

// UPDATED: final architecture - sirf JWT Bearer; access + refresh token JSON mein, koi cookie ya CSRF nahi
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicies.Sensitive);

        // Anonymous: identity comes from credentials, a one-time link token, or the refresh token in the JSON body.
        group.MapPost("/register", Register).WithName("Register")
            .WithSummary("Register account")
            .WithDescription("Anonymous. Creates an account and sends an email-verification link. Always answers 202 so existing and new emails are indistinguishable.")
            .AllowAnonymous();
        group.MapPost("/confirm-email", ConfirmEmail).WithName("ConfirmEmail")
            .WithSummary("Confirm email address")
            .WithDescription("Anonymous. Consumes the single-use verification token from the email link.")
            .AllowAnonymous();
        group.MapPost("/resend-confirmation", ResendConfirmation).WithName("ResendConfirmation")
            .WithSummary("Resend verification email")
            .WithDescription("Anonymous. Sends a new verification link when the account exists and is unverified; always answers 202.")
            .AllowAnonymous();
        group.MapPost("/login", Login).WithName("Login")
            .WithSummary("Authenticate user")
            .WithDescription("Anonymous. Validates user credentials and returns a JWT access token (15 minutes) and a JWT refresh token (7 days). Failures are one generic 401; repeated failures lock the account temporarily.")
            .AllowAnonymous();
        group.MapPost("/refresh", Refresh).WithName("RefreshSession")
            .WithSummary("Refresh authentication tokens")
            .WithDescription("Anonymous. Validates and rotates a refresh JWT sent in the JSON body and returns a new token pair. Each refresh token works exactly once: a replay revokes the session, and a racing duplicate within the grace window gets 409 without a new token.")
            .ProducesConflict()
            .AllowAnonymous();
        group.MapPost("/logout", Logout).WithName("Logout")
            .WithSummary("Sign out this session")
            .WithDescription("Anonymous. Revokes the session that owns the supplied refresh token. Unknown tokens are ignored.")
            .AllowAnonymous();
        group.MapPost("/forgot-password", ForgotPassword).WithName("ForgotPassword")
            .WithSummary("Request password reset")
            .WithDescription("Anonymous. Sends a reset link when the account exists; always answers 202.")
            .AllowAnonymous();
        group.MapPost("/reset-password", ResetPassword).WithName("ResetPassword")
            .WithSummary("Reset password")
            .WithDescription("Anonymous. Sets a new password with the single-use reset token and revokes every session of the user.")
            .AllowAnonymous();

        // Bearer access token required.
        group.MapGet("/me", Me).WithName("GetCurrentUser")
            .WithSummary("Get current user")
            .WithDescription("Returns the profile of the authenticated user.");
        group.MapPost("/logout-all", LogoutAll).WithName("LogoutAll")
            .WithSummary("Sign out everywhere")
            .WithDescription("Revokes every session (device) of the authenticated user.");
        group.MapGet("/sessions", ListSessions).WithName("ListSessions")
            .WithSummary("List active sessions")
            .WithDescription("Returns the authenticated user's active sessions; the current one is flagged.");
        group.MapDelete("/sessions/{sessionId:guid}", RevokeSession).WithName("RevokeSession")
            .WithSummary("Revoke a session")
            .WithDescription("Revokes one of the authenticated user's own sessions.");
        group.MapPost("/change-password", ChangePassword).WithName("ChangePassword")
            .WithSummary("Change password")
            .WithDescription("Changes the password, revokes all sessions and returns a brand-new token pair for this device.");
        group.MapPost("/deactivate", Deactivate).WithName("DeactivateAccount")
            .WithSummary("Deactivate account")
            .WithDescription("Deactivates the authenticated account after password confirmation and revokes all sessions. The last active Owner of a tenant cannot deactivate.")
            .ProducesConflict();
        return app;
    }

    // Always 202: new and existing emails are indistinguishable (validation errors depend only on the submitted password).
    private static async Task<Accepted> Register(RegisterRequest request, AccountService accounts, CancellationToken ct)
    {
        await accounts.RegisterAsync(request.Email, request.DisplayName, request.Password, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<NoContent> ConfirmEmail(ConfirmEmailRequest request, AccountService accounts, CancellationToken ct)
    {
        await accounts.ConfirmEmailAsync(request.UserId, request.Token, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Accepted> ResendConfirmation(EmailRequest request, AccountService accounts, CancellationToken ct)
    {
        await accounts.ResendVerificationAsync(request.Email, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Ok<AuthResponse>> Login(LoginRequest request, HttpContext http, SessionService sessions, CancellationToken ct)
    {
        var (tokens, user) = await sessions.LoginAsync(request.Email, request.Password, UserAgent(http), ct);
        return TokenResponse(http, tokens, user);
    }

    private static async Task<Ok<AuthResponse>> Refresh(RefreshRequest request, HttpContext http, SessionService sessions, CancellationToken ct)
    {
        var (tokens, user) = await sessions.RefreshAsync(request.RefreshToken, UserAgent(http), ct);
        return TokenResponse(http, tokens, user);
    }

    private static async Task<NoContent> Logout(LogoutRequest request, SessionService sessions, CancellationToken ct)
    {
        await sessions.LogoutAsync(request.RefreshToken, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Accepted> ForgotPassword(EmailRequest request, AccountService accounts, CancellationToken ct)
    {
        await accounts.ForgotPasswordAsync(request.Email, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<NoContent> ResetPassword(ResetPasswordRequest request, AccountService accounts, CancellationToken ct)
    {
        await accounts.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UserInfo>> Me(ICurrentUser current, AccountService accounts, CancellationToken ct) =>
        TypedResults.Ok(ToInfo(await accounts.GetAsync(current.UserId, ct)));

    private static async Task<NoContent> LogoutAll(ICurrentUser current, SessionService sessions, CancellationToken ct)
    {
        await sessions.LogoutAllAsync(current.UserId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<SessionResponse>>> ListSessions(ICurrentUser current, SessionService sessions, CancellationToken ct)
    {
        var list = await sessions.ListAsync(current.UserId, current.SessionId, ct);
        return TypedResults.Ok(list.Select(s => new SessionResponse(s.Id, s.UserAgent, s.CreatedAtUtc, s.LastUsedAtUtc, s.IsCurrent)).ToList());
    }

    private static async Task<NoContent> RevokeSession(Guid sessionId, ICurrentUser current, SessionService sessions, CancellationToken ct)
    {
        await sessions.RevokeAsync(current.UserId, sessionId, ct);
        return TypedResults.NoContent();
    }

    // Every session is revoked; the caller immediately gets a brand-new one so the SPA stays signed in on this device.
    private static async Task<Ok<AuthResponse>> ChangePassword(
        ChangePasswordRequest request, ICurrentUser current, HttpContext http, AccountService accounts, SessionService sessions, CancellationToken ct)
    {
        var user = await accounts.ChangePasswordAsync(current.UserId, request.CurrentPassword, request.NewPassword, ct);
        return TokenResponse(http, await sessions.StartAsync(user, UserAgent(http), ct), user);
    }

    private static async Task<NoContent> Deactivate(DeactivateAccountRequest request, ICurrentUser current, AccountService accounts, CancellationToken ct)
    {
        await accounts.DeactivateAsync(current.UserId, request.Password, ct);
        return TypedResults.NoContent();
    }

    // Responses carrying tokens must never be stored by browsers or intermediaries.
    private static Ok<AuthResponse> TokenResponse(HttpContext http, SessionTokens tokens, User user)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(new AuthResponse(tokens.AccessToken, "Bearer", tokens.AccessTokenExpiresAtUtc, tokens.RefreshToken, tokens.RefreshTokenExpiresAtUtc, ToInfo(user)));
    }

    private static string? UserAgent(HttpContext http) => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    private static UserInfo ToInfo(User user) => new(user.Id, user.Email, user.DisplayName);
}
