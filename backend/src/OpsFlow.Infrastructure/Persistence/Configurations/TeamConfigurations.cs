using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Team;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class MemberProfileConfiguration : IEntityTypeConfiguration<MemberProfile>
{
    public void Configure(EntityTypeBuilder<MemberProfile> b)
    {
        b.ToTable("MemberProfiles");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.JobTitle).HasMaxLength(100);
        b.Property(p => p.Department).HasMaxLength(100);
        b.Property(p => p.Phone).HasMaxLength(40);
        b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.TenantId, p.MembershipId }).IsUnique(); // one profile per membership
    }
}

internal sealed class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> b)
    {
        b.ToTable("Skills", t => t.HasCheckConstraint("CK_Skills_Name", "length(trim(\"Name\")) > 0"));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Name).HasMaxLength(Skill.MaxNameLength).IsRequired();
        b.Property(s => s.NormalizedName).HasMaxLength(Skill.MaxNameLength).IsRequired();
        b.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(s => new { s.TenantId, s.Id }); // target of tenant-safe composite foreign keys
        b.HasIndex(s => new { s.TenantId, s.NormalizedName }).IsUnique();
    }
}

internal sealed class MemberSkillConfiguration : IEntityTypeConfiguration<MemberSkill>
{
    public void Configure(EntityTypeBuilder<MemberSkill> b)
    {
        b.ToTable("MemberSkills");
        b.HasKey(s => new { s.TenantId, s.MembershipId, s.SkillId });
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Skill>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.SkillId })
            .HasPrincipalKey(k => new { k.TenantId, k.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(s => new { s.TenantId, s.SkillId }); // directory filter by skill
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("AuditEvents");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Action).HasConversion<string>().HasMaxLength(40);
        b.Property(e => e.Detail).HasMaxLength(AuditEvent.MaxDetailLength);
        b.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.TargetMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.TargetMembershipId, e.OccurredAtUtc });
    }
}
