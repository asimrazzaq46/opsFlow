using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.ToTable(
            "Memberships",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_Memberships_Role",
                    "\"Role\" IN ('TenantOwner','Manager','Agent','Customer')"
                );
                t.HasCheckConstraint(
                    "CK_Memberships_Status",
                    "\"Status\" IN ('Active','Inactive')"
                );
            }
        );
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.HasAlternateKey(m => new { m.TenantId, m.Id }); // target of tenant-scoped composite FKs
        b.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        b.HasIndex(m => m.UserId);
        b.HasIndex(m => new
        {
            m.TenantId,
            m.Role,
            m.Status,
        });
    }
}
