using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class UserExternalIdentityConfiguration : IEntityTypeConfiguration<UserExternalIdentity>
{
    public void Configure(EntityTypeBuilder<UserExternalIdentity> b)
    {
        b.ToTable(
            "UserExternalIdentities",
            t => t.HasCheckConstraint("CK_UserExternalIdentities_Provider", "\"Provider\" IN ('Google')")
        );
        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedNever();
        b.Property(i => i.Provider).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.ProviderSubject)
            .HasMaxLength(UserExternalIdentity.MaxSubjectLength)
            .IsRequired();

        b.HasOne<User>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
        // Users are global, so both rules are global: one owner per provider account, one account per provider per user.
        b.HasIndex(i => new { i.Provider, i.ProviderSubject }).IsUnique();
        b.HasIndex(i => new { i.UserId, i.Provider }).IsUnique();
    }
}
