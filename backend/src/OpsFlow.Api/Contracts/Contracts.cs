using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

// Request contracts: validation attributes sit on the record parameters (MVC binds records through the constructor and rejects property-targeted attributes).
public sealed record RegisterRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128)] string Password,
    [Required, StringLength(100, MinimumLength = 2)] string DisplayName);

public sealed record EmailRequest([Required, EmailAddress, StringLength(256)] string Email);

public sealed record ConfirmEmailRequest([Required] Guid UserId, [Required] string Token);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, StringLength(128)] string Password);

/// <summary>The refresh token travels in the HTTPS JSON body (no cookies are used anywhere).</summary>
public sealed record RefreshRequest([Required, StringLength(2048)] string RefreshToken);

public sealed record LogoutRequest([Required, StringLength(2048)] string RefreshToken);

public sealed record ResetPasswordRequest(
    [Required, EmailAddress] string Email,
    [Required] string Token,
    [Required, StringLength(128)] string NewPassword);

public sealed record ChangePasswordRequest(
    [Required, StringLength(128)] string CurrentPassword,
    [Required, StringLength(128)] string NewPassword);

public sealed record DeactivateAccountRequest([Required, StringLength(128)] string Password);

public sealed record UserInfo(Guid Id, string Email, string DisplayName);

/// <summary>Short-lived access token for the Authorization: Bearer header plus a single-use rotating refresh JWT. Keep both in memory.</summary>
public sealed record AuthResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc, UserInfo User);

public sealed record SessionResponse(Guid Id, string? UserAgent, DateTime CreatedAtUtc, DateTime LastUsedAtUtc, bool IsCurrent);

public sealed record CreateOrganizationRequest([Required, StringLength(100, MinimumLength = 2)] string Name);

public sealed record AcceptInvitationRequest([Required, StringLength(200)] string Token);

public sealed record CreateInvitationRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record ChangeRoleRequest([EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record SetStatusRequest(bool IsActive);

public sealed record DefaultsResult(int RecordsAdded);
