using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Name).HasMaxLength(100).IsRequired();
        b.Property(t => t.Slug).HasMaxLength(63).IsRequired();
        b.Property(t => t.OwnerChangeStamp).IsConcurrencyToken();
        b.HasIndex(t => t.Slug).IsUnique();
    }
}
