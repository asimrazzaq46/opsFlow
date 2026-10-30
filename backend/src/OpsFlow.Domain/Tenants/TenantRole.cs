namespace OpsFlow.Domain.Tenants;

public static class TenantRoleExtensions
{
    public static bool IsStaff(this TenantRole role) =>
        role is TenantRole.TenantOwner or TenantRole.Manager or TenantRole.Agent;
}
