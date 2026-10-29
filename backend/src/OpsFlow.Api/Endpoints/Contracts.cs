using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Endpoints;

// Public, top-level request contracts: the minimal-API validation source generator only discovers accessible types.
public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: Required, StringLength(128)] string Password,
    [property: Required, StringLength(100, MinimumLength = 2)] string DisplayName);

public sealed record EmailRequest([property: Required, EmailAddress, StringLength(256)] string Email);

public sealed record ConfirmEmailRequest([property: Required] Guid UserId, [property: Required] string Token);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    bool RememberMe = false);

public sealed record ResetPasswordRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Token,
    [property: Required, StringLength(128)] string NewPassword);

public sealed record UserInfo(Guid Id, string Email, string DisplayName);

public sealed record CreateOrganizationRequest([property: Required, StringLength(100, MinimumLength = 2)] string Name);

public sealed record AcceptInvitationRequest([property: Required, StringLength(200)] string Token);

public sealed record CreateInvitationRequest(
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record ChangeRoleRequest([property: EnumDataType(typeof(TenantRole))] TenantRole Role);

public sealed record SetStatusRequest(bool IsActive);
