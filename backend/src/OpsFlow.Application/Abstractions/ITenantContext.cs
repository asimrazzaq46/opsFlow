using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Abstractions;

/// <summary>The tenant of the current request. TenantId is null when no tenant is resolved, which makes tenant queries return nothing.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? MembershipId { get; }
    TenantRole? Role { get; }

    /// <summary>Explicitly scopes trusted server-side flows (onboarding, invitation acceptance) to a tenant.</summary>
    IDisposable Begin(Guid tenantId);
}
