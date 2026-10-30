using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Abstractions;

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? MembershipId { get; private set; }
    public TenantRole? Role { get; private set; }

    /// <summary>Called only by tenant authorization after the active membership was validated.</summary>
    public void Set(Guid tenantId, Guid membershipId, TenantRole role)
    {
        TenantId = tenantId;
        MembershipId = membershipId;
        Role = role;
    }

    public IDisposable Begin(Guid tenantId)
    {
        var previous = (TenantId, MembershipId, Role);
        (TenantId, MembershipId, Role) = (tenantId, null, null);
        return new Restore(this, previous);
    }

    private sealed class Restore(
        TenantContext owner,
        (Guid? TenantId, Guid? MembershipId, TenantRole? Role) previous
    ) : IDisposable
    {
        public void Dispose() => (owner.TenantId, owner.MembershipId, owner.Role) = previous;
    }
}
