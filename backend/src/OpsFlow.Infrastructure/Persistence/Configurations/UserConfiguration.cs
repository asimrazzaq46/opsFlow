using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users", t =>
        {
            t.HasCheckConstraint("CK_Users_Status", "\"Status\" IN ('Active','Deactivated')");
            t.HasCheckConstraint("CK_Users_FailedLoginCount", "\"FailedLoginCount\" >= 0");
        });
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedNever();
        b.Property(u => u.Email).HasMaxLength(256).IsRequired();
        b.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        b.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(u => u.NormalizedEmail).IsUnique();
    }
}
