using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Team;

/// <summary>Workspace-specific employee details. Name and email stay on the global User and are never copied here.</summary>
public sealed class MemberProfile : ITenantOwned
{
    private MemberProfile() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid MembershipId { get; private set; }
    public string? JobTitle { get; private set; }
    public string? Department { get; private set; }
    public string? Phone { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static MemberProfile Create(Guid tenantId, Guid membershipId, DateTime nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MembershipId = membershipId,
            UpdatedAtUtc = nowUtc,
        };

    public void Update(string? jobTitle, string? department, string? phone, DateTime nowUtc)
    {
        JobTitle = Clean(jobTitle);
        Department = Clean(department);
        Phone = Clean(phone);
        UpdatedAtUtc = nowUtc;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A workspace's skill tag. Deactivated tags stay on the members that have them but cannot be newly assigned.</summary>
public sealed class Skill : ITenantOwned
{
    public const int MaxNameLength = 50;

    private Skill()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public string NormalizedName { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Skill Create(Guid tenantId, string name, DateTime nowUtc)
    {
        var skill = new Skill
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsActive = true,
            CreatedAtUtc = nowUtc,
        };
        skill.Rename(name);
        return skill;
    }

    public void Rename(string name)
    {
        Name = name.Trim();
        NormalizedName = Normalize(name);
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}

public sealed class MemberSkill : ITenantOwned
{
    private MemberSkill() { }

    public Guid TenantId { get; private set; }
    public Guid MembershipId { get; private set; }
    public Guid SkillId { get; private set; }
    public DateTime AddedAtUtc { get; private set; }

    public static MemberSkill Create(Guid tenantId, Guid membershipId, Guid skillId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId,
            MembershipId = membershipId,
            SkillId = skillId,
            AddedAtUtc = nowUtc,
        };
}

public enum AuditAction
{
    MemberRoleChanged,
    MemberStatusChanged,
    MemberProfileUpdated,
    MemberSkillsChanged,
    SkillCreated,
    SkillUpdated,
}

/// <summary>Append-only record of an administrative change in a workspace. Holds no credentials or tokens.</summary>
public sealed class AuditEvent : ITenantOwned
{
    public const int MaxDetailLength = 1000;

    private AuditEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public AuditAction Action { get; private set; }
    public Guid? TargetMembershipId { get; private set; }
    public Guid? TargetSkillId { get; private set; }
    public string? Detail { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static AuditEvent Create(
        Guid tenantId,
        Guid actorUserId,
        AuditAction action,
        Guid? targetMembershipId,
        Guid? targetSkillId,
        string? detail,
        DateTime nowUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Action = action,
            TargetMembershipId = targetMembershipId,
            TargetSkillId = targetSkillId,
            Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail,
            OccurredAtUtc = nowUtc,
        };
}
