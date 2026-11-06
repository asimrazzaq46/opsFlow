using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

/// <summary>ExpectedRole is the role the administrator saw; when given and no longer current, the change is refused (409 MEMBER_CHANGED).</summary>
public sealed record ChangeRoleRequest(
    [EnumDataType(typeof(TenantRole))] TenantRole Role,
    [EnumDataType(typeof(TenantRole))] TenantRole? ExpectedRole = null
);

/// <summary>ExpectedRole is the role the administrator saw; when given and no longer current, the change is refused (409 MEMBER_CHANGED).</summary>
/// <remarks>Suspending an active Agent who holds open work needs AcknowledgeOpenWork = true after the administrator has seen that work.</remarks>
public sealed record SetMemberStatusRequest(
    bool IsActive,
    [EnumDataType(typeof(TenantRole))] TenantRole? ExpectedRole = null,
    bool AcknowledgeOpenWork = false
);
