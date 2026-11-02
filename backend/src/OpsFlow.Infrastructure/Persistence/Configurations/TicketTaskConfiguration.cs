using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketTaskConfiguration : IEntityTypeConfiguration<TicketTask>
{
    public void Configure(EntityTypeBuilder<TicketTask> b)
    {
        b.ToTable(
            "TicketTasks",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_TicketTasks_Status",
                    "\"Status\" IN ('Todo','InProgress','Done')"
                );
                t.HasCheckConstraint("CK_TicketTasks_SortOrder", "\"SortOrder\" > 0");
                t.HasCheckConstraint("CK_TicketTasks_Title", "length(trim(\"Title\")) > 0");
            }
        );
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Title).HasMaxLength(TicketTask.TitleMaxLength).IsRequired();
        b.Property(t => t.Description).HasMaxLength(TicketTask.DescriptionMaxLength);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(t => t.Version).IsConcurrencyToken();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        // Composite FKs: the database itself rejects a ticket or membership of another tenant.
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.TicketId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.AssigneeMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.CreatedByMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.UpdatedByMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Not unique: a reorder rewrites many positions in one statement batch; writers are serialized by the parent ticket row lock.
        b.HasIndex(t => new
        {
            t.TenantId,
            t.TicketId,
            t.SortOrder,
        });
        b.HasIndex(t => new
        {
            t.TenantId,
            t.AssigneeMembershipId,
            t.Status,
        });
    }
}
