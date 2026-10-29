using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.ToTable("Invitations", t => t.HasCheckConstraint("CK_Invitations_Role", "\"Role\" IN ('TenantOwner','Manager','Agent')"));
        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedNever();
        b.Property(i => i.Email).HasMaxLength(256).IsRequired();
        b.Property(i => i.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();

        b.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
        // Composite FK: the inviter must be a membership of the SAME tenant (enforced by the database).
        b.HasOne<Membership>().WithMany().HasForeignKey(i => new { i.TenantId, i.InvitedByMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.AcceptedByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(i => i.TokenHash).IsUnique();
        // At most one open invitation per tenant and email.
        b.HasIndex(i => new { i.TenantId, i.NormalizedEmail }).IsUnique()
            .HasFilter("\"AcceptedAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL");
    }
}
