using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tenants;

public sealed class Membership : ITenantOwned
{
    private Membership() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public TenantRole Role { get; private set; }
    public MembershipStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public bool IsActiveOwner =>
        Role == TenantRole.TenantOwner && Status == MembershipStatus.Active;

    public static Membership Create(Guid tenantId, Guid userId, TenantRole role, DateTime nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            Status = MembershipStatus.Active,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    public void ChangeRole(TenantRole role, DateTime nowUtc)
    {
        Role = role;
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        Status = isActive ? MembershipStatus.Active : MembershipStatus.Inactive;
        UpdatedAtUtc = nowUtc;
    }
}
