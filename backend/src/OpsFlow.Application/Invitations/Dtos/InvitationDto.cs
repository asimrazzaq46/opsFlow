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

/// <summary>CreatedAtUtc is when the invitation was first created; SentAtUtc is when its current link was last emailed (create or resend).</summary>
public sealed record InvitationDto(
    Guid Id,
    string Email,
    TenantRole Role,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime SentAtUtc
);

/// <summary>Lifecycle state derived from the persisted invitation: accepted, revoked, open but past its expiry, or still pending.</summary>
public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

/// <summary>One row of the complete invitation history. Role is the role the invitation was issued for; it never follows later membership changes.</summary>
public sealed record InvitationHistoryDto(
    Guid Id,
    string Email,
    TenantRole Role,
    InvitationStatus Status,
    DateTime CreatedAtUtc,
    DateTime SentAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? RevokedAtUtc
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
