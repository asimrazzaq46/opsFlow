using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketSlaCycleConfiguration : IEntityTypeConfiguration<TicketSlaCycle>
{
    public void Configure(EntityTypeBuilder<TicketSlaCycle> b)
    {
        b.ToTable(
            "TicketSlaCycles",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_TicketSlaCycles_Reason",
                    "\"Reason\" IN ('Initial','Reopened')"
                );
                t.HasCheckConstraint("CK_TicketSlaCycles_CycleNumber", "\"CycleNumber\" > 0");
            }
        );
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Reason).HasConversion<string>().HasMaxLength(20);

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SlaPolicy>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.SlaPolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(c => new { c.TicketId, c.CycleNumber }).IsUnique();
        b.HasIndex(c => c.TicketId).IsUnique().HasFilter("\"ResolvedAtUtc\" IS NULL"); // never two ACTIVE cycles (concurrent reopen)
        b.HasIndex(c => new { c.TenantId, c.ResolutionDueAtUtc })
            .HasFilter("\"ResolvedAtUtc\" IS NULL"); // overdue queries
        // UPDATED: Milestone 4 - SLA monitor scans only cycles whose deadline work is still open
        b.HasIndex(c => c.ResolutionDueAtUtc)
            .HasFilter("\"ResolutionBreachedAtUtc\" IS NULL")
            .HasDatabaseName("IX_TicketSlaCycles_ResolutionMonitor");
        b.HasIndex(c => c.ResponseDueAtUtc)
            .HasFilter("\"ResponseBreachedAtUtc\" IS NULL AND \"ResponseDueAtUtc\" IS NOT NULL")
            .HasDatabaseName("IX_TicketSlaCycles_ResponseMonitor");
    }
}
