using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("Customers");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.HasAlternateKey(c => new { c.TenantId, c.Id });
        b.Property(c => c.Name).HasMaxLength(150).IsRequired();
        b.Property(c => c.Email).HasMaxLength(256).IsRequired();
        b.Property(c => c.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(c => c.Phone).HasMaxLength(50);

        b.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(c => new { c.TenantId, c.NormalizedEmail }).IsUnique();
        b.HasIndex(c => new { c.TenantId, c.UserId }).IsUnique().HasFilter("\"UserId\" IS NOT NULL"); // a user maps to at most one contact per tenant
    }
}
