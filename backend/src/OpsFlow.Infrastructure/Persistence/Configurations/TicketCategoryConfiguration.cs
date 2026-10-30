using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketCategoryConfiguration : IEntityTypeConfiguration<TicketCategory>
{
    public void Configure(EntityTypeBuilder<TicketCategory> b)
    {
        b.ToTable("TicketCategories");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.HasAlternateKey(c => new { c.TenantId, c.Id });
        b.Property(c => c.Name).HasMaxLength(100).IsRequired();
        b.Property(c => c.NormalizedName).HasMaxLength(100).IsRequired();

        b.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(c => new { c.TenantId, c.NormalizedName }).IsUnique();
    }
}
