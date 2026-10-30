using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Domain.Users;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimitPolicies.Sensitive)]
[Authorize]
public sealed class AuthController(
    AccountService accounts,
    SessionService sessions,
    ICurrentUser current
) : ControllerBase
{
    [HttpPost("register", Name = "Register")]
    [AllowAnonymous]
    [EndpointSummary("Register account")]
    [EndpointDescription(
        "Anonymous. Creates an account and sends an email-verification link. Always answers 202 so existing and new emails are indistinguishable."
    )]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        await accounts.RegisterAsync(request.Email, request.DisplayName, request.Password, ct);
        return Accepted();
    }

    [HttpPost("confirm-email", Name = "ConfirmEmail")]
    [AllowAnonymous]
    [EndpointSummary("Confirm email address")]
    [EndpointDescription(
        "Anonymous. Consumes the single-use verification token from the email link."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken ct)
    {
        await accounts.ConfirmEmailAsync(request.UserId, request.Token, ct);
        return NoContent();
    }

    [HttpPost("resend-confirmation", Name = "ResendConfirmation")]
    [AllowAnonymous]
    [EndpointSummary("Resend verification email")]
    [EndpointDescription(
        "Anonymous. Sends a new verification link when the account exists and is unverified; always answers 202."
    )]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendConfirmation(EmailRequest request, CancellationToken ct)
    {
        await accounts.ResendVerificationAsync(request.Email, ct);
        return Accepted();
    }

    [HttpPost("login", Name = "Login")]
    [AllowAnonymous]
    [EndpointSummary("Authenticate user")]
    [EndpointDescription(
        "Anonymous. Validates user credentials and returns a JWT access token (15 minutes) and a JWT refresh token (7 days). Failures are one generic 401; repeated failures lock the account temporarily."
    )]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var (tokens, user) = await sessions.LoginAsync(
            request.Email,
            request.Password,
            UserAgent,
            ct
        );
        return TokenResponse(tokens, user);
    }

    [HttpPost("refresh", Name = "RefreshSession")]
    [AllowAnonymous]
    [EndpointSummary("Refresh authentication tokens")]
    [EndpointDescription(
        "Anonymous. Validates and rotates a refresh JWT sent in the JSON body and returns a new token pair. Each refresh token works exactly once: a replay revokes the session, and a racing duplicate within the grace window gets 409 without a new token."
    )]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<AuthResponse>> RefreshSession(
        RefreshRequest request,
        CancellationToken ct
    )
    {
        var (tokens, user) = await sessions.RefreshAsync(request.RefreshToken, UserAgent, ct);
        return TokenResponse(tokens, user);
    }

    [HttpPost("logout", Name = "Logout")]
    [AllowAnonymous]
    [EndpointSummary("Sign out this session")]
    [EndpointDescription(
        "Anonymous. Revokes the session that owns the supplied refresh token. Unknown tokens are ignored."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await sessions.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [HttpPost("forgot-password", Name = "ForgotPassword")]
    [AllowAnonymous]
    [EndpointSummary("Request password reset")]
    [EndpointDescription(
        "Anonymous. Sends a reset link when the account exists; always answers 202."
    )]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(EmailRequest request, CancellationToken ct)
    {
        await accounts.ForgotPasswordAsync(request.Email, ct);
        return Accepted();
    }

    [HttpPost("reset-password", Name = "ResetPassword")]
    [AllowAnonymous]
    [EndpointSummary("Reset password")]
    [EndpointDescription(
        "Anonymous. Sets a new password with the single-use reset token and revokes every session of the user."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordRequest request,
        CancellationToken ct
    )
    {
        await accounts.ResetPasswordAsync(request.Email, request.Token, request.NewPassword, ct);
        return NoContent();
    }

    [HttpGet("me", Name = "GetCurrentUser")]
    [Authorize]
    [EndpointSummary("Get current user")]
    [EndpointDescription("Returns the profile of the authenticated user.")]
    public async Task<ActionResult<UserInfo>> GetCurrentUser(CancellationToken ct)
    {
        return Ok(ToInfo(await accounts.GetAsync(current.UserId, ct)));
    }

    [HttpPost("logout-all", Name = "LogoutAll")]
    [Authorize]
    [EndpointSummary("Sign out everywhere")]
    [EndpointDescription("Revokes every session (device) of the authenticated user.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await sessions.LogoutAllAsync(current.UserId, ct);
        return NoContent();
    }

    [HttpGet("sessions", Name = "ListSessions")]
    [Authorize]
    [EndpointSummary("List active sessions")]
    [EndpointDescription(
        "Returns the authenticated user's active sessions; the current one is flagged."
    )]
    public async Task<ActionResult<List<SessionResponse>>> ListSessions(CancellationToken ct)
    {
        var list = await sessions.ListAsync(current.UserId, current.SessionId, ct);
        return Ok(
            list.Select(s => new SessionResponse(
                    s.Id,
                    s.UserAgent,
                    s.CreatedAtUtc,
                    s.LastUsedAtUtc,
                    s.IsCurrent
                ))
                .ToList()
        );
    }

    [HttpDelete("sessions/{sessionId:guid}", Name = "RevokeSession")]
    [Authorize]
    [EndpointSummary("Revoke a session")]
    [EndpointDescription("Revokes one of the authenticated user's own sessions.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        await sessions.RevokeAsync(current.UserId, sessionId, ct);
        return NoContent();
    }

    [HttpPost("change-password", Name = "ChangePassword")]
    [Authorize]
    [EndpointSummary("Change password")]
    [EndpointDescription(
        "Changes the password, revokes all sessions and returns a brand-new token pair for this device."
    )]
    public async Task<ActionResult<AuthResponse>> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken ct
    )
    {
        // Every session is revoked; the caller immediately gets a brand-new one so the SPA stays signed in on this device.
        var user = await accounts.ChangePasswordAsync(
            current.UserId,
            request.CurrentPassword,
            request.NewPassword,
            ct
        );
        return TokenResponse(await sessions.StartAsync(user, UserAgent, ct), user);
    }

    [HttpPost("deactivate", Name = "DeactivateAccount")]
    [Authorize]
    [EndpointSummary("Deactivate account")]
    [EndpointDescription(
        "Deactivates the authenticated account after password confirmation and revokes all sessions. The last active Owner of a tenant cannot deactivate."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<IActionResult> DeactivateAccount(
        DeactivateAccountRequest request,
        CancellationToken ct
    )
    {
        await accounts.DeactivateAsync(current.UserId, request.Password, ct);
        return NoContent();
    }

    private string? UserAgent =>
        Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    // Responses carrying tokens must never be stored by browsers or intermediaries.
    private ActionResult<AuthResponse> TokenResponse(SessionTokens tokens, User user)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(
            new AuthResponse(
                tokens.AccessToken,
                "Bearer",
                tokens.AccessTokenExpiresAtUtc,
                tokens.RefreshToken,
                tokens.RefreshTokenExpiresAtUtc,
                ToInfo(user)
            )
        );
    }

    private static UserInfo ToInfo(User user) => new(user.Id, user.Email, user.DisplayName);
}
