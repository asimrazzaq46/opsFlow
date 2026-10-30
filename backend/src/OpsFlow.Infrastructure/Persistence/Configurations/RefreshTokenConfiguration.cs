using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(t => t.UsedAtUtc).IsConcurrencyToken(); // atomic rotation: of N concurrent uses exactly one persists

        b.HasOne<UserSession>()
            .WithMany()
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.SessionId);
    }
}
