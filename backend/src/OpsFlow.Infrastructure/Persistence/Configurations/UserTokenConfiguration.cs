using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> b)
    {
        b.ToTable(
            "UserTokens",
            t =>
                t.HasCheckConstraint(
                    "CK_UserTokens_Purpose",
                    "\"Purpose\" IN ('EmailVerification','PasswordReset')"
                )
        );
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(30);
        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(t => t.UsedAtUtc).IsConcurrencyToken(); // two concurrent uses of one token cannot both persist

        b.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => new { t.UserId, t.Purpose });
    }
}
