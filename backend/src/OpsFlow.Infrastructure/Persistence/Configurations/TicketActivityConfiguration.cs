using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketActivityConfiguration : IEntityTypeConfiguration<TicketActivity>
{
    public void Configure(EntityTypeBuilder<TicketActivity> b)
    {
        b.ToTable("TicketActivities", t => t.HasCheckConstraint("CK_TicketActivities_Sequence", "\"Sequence\" > 0"));
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(a => a.OldValue).HasMaxLength(200);
        b.Property(a => a.NewValue).HasMaxLength(200);

        b.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>().WithMany().HasForeignKey(a => new { a.TenantId, a.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(a => new { a.TicketId, a.Sequence }).IsUnique(); // deterministic timeline order, no duplicate positions
    }
}
