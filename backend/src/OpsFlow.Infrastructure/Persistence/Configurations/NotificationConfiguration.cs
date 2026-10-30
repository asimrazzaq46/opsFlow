using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).ValueGeneratedNever();
        b.Property(n => n.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(n => n.Subject).HasMaxLength(200).IsRequired();
        b.Property(n => n.Detail).HasMaxLength(100);
        b.Property(n => n.DedupKey).HasMaxLength(100).IsRequired();

        b.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>().WithMany().HasForeignKey(n => new { n.TenantId, n.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(n => new { n.TenantId, n.UserId, n.DedupKey }).IsUnique(); // retries/concurrent writers cannot duplicate
        b.HasIndex(n => new { n.TenantId, n.UserId, n.CreatedAtUtc, n.Id }); // history paging
        b.HasIndex(n => new { n.TenantId, n.UserId }).HasFilter("\"ReadAtUtc\" IS NULL"); // unread count
    }
}
