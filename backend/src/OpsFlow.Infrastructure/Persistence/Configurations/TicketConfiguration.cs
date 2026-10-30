using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("Tickets", t =>
        {
            t.HasCheckConstraint("CK_Tickets_Status", "\"Status\" IN ('Open','Assigned','InProgress','OnHold','Resolved','Closed')");
            t.HasCheckConstraint("CK_Tickets_Priority", "\"Priority\" IN ('Low','Medium','High','Urgent')");
            t.HasCheckConstraint("CK_Tickets_Number", "\"Number\" > 0");
        });
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.HasAlternateKey(t => new { t.TenantId, t.Id }); // target of tenant-consistent composite FKs
        b.Property(t => t.Title).HasMaxLength(200).IsRequired();
        b.Property(t => t.Description).HasMaxLength(4000).IsRequired();
        b.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(t => t.Version).IsConcurrencyToken();

        b.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TicketCategory>().WithMany().HasForeignKey(t => new { t.TenantId, t.CategoryId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(t => new { t.TenantId, t.CustomerId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        b.HasIndex(t => new { t.TenantId, t.Status });
        b.HasIndex(t => new { t.TenantId, t.CreatedAtUtc, t.Id });
        b.HasIndex(t => new { t.TenantId, t.CreatedByUserId });
    }
}
