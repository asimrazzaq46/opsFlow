using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Organizations;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Invitations;

public sealed record InvitationDto(
    Guid Id,
    string Email,
    TenantRole Role,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc
);

public enum InvitationPreviewState
{
    Valid,
    Expired,
    Unavailable,
}

/// <summary>
/// What the holder of an invitation token may see before signing in. Details are present only for a <see cref="InvitationPreviewState.Valid"/>
/// invitation. <c>Role</c> is the role name as text so the shared non-nullable TenantRole schema stays unchanged.
/// </summary>
public sealed record InvitationPreviewDto(
    InvitationPreviewState State,
    string? WorkspaceName,
    string? Role,
    string? Email,
    DateTime? ExpiresAtUtc
);
