using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Memberships;

[OpsFlow.Realtime.RealtimeEvent("membership.changed")]
public sealed record MembershipChanged(Guid TenantId, Guid MembershipId);

public sealed record MemberDto(
    Guid Id,
    Guid UserId,
    string Email,
    string DisplayName,
    TenantRole Role,
    bool IsActive,
    DateTime CreatedAtUtc
);
