using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketAssignmentConfiguration : IEntityTypeConfiguration<TicketAssignment>
{
    public void Configure(EntityTypeBuilder<TicketAssignment> b)
    {
        b.ToTable("TicketAssignments");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        // Composite FKs make the database itself reject a ticket/agent pair from different tenants.
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.AssignedByMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.UnassignedByMembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // An agent can hold at most ONE active assignment per ticket.
        b.HasIndex(a => new { a.TicketId, a.MembershipId })
            .IsUnique()
            .HasFilter("\"UnassignedAtUtc\" IS NULL");
        b.HasIndex(a => a.MembershipId).HasFilter("\"UnassignedAtUtc\" IS NULL"); // agent visibility lookups
    }
}
