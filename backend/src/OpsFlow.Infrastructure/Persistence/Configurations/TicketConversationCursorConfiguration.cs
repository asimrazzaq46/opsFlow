using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketConversationCursorConfiguration
    : IEntityTypeConfiguration<TicketConversationCursor>
{
    public void Configure(EntityTypeBuilder<TicketConversationCursor> b)
    {
        b.ToTable(
            "TicketConversationCursors",
            t =>
            {
                t.HasCheckConstraint("CK_TicketConversationCursors_Read", "\"ReadSequence\" >= 0");
                // Reading implies delivery, so the delivered position can never be behind the read one.
                t.HasCheckConstraint(
                    "CK_TicketConversationCursors_Delivered",
                    "\"DeliveredSequence\" >= \"ReadSequence\""
                );
            }
        );
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(
                c => new
                {
                    c.TenantId,
                    c.TicketId,
                    c.UserId,
                },
                "IX_TicketConversationCursors_Ticket_User"
            )
            .IsUnique(); // one cursor per user and ticket; concurrent first acknowledgements collide here
        b.HasIndex(c => new { c.TenantId, c.UserId }); // workspace-wide unread totals
    }
}
