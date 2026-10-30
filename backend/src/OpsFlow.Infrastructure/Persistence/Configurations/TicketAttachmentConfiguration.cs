using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> b)
    {
        b.ToTable(
            "TicketAttachments",
            t => t.HasCheckConstraint("CK_TicketAttachments_Size", "\"SizeBytes\" > 0")
        );
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.FileName).HasMaxLength(200).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        b.Property(a => a.StorageKey).HasMaxLength(100).IsRequired();
        b.Property(a => a.Sha256).HasMaxLength(64).IsRequired();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(a => new
        {
            a.TenantId,
            a.TicketId,
            a.CreatedAtUtc,
            a.Id,
        });
        b.HasIndex(a => a.StorageKey).IsUnique();
    }
}
