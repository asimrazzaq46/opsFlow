using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> b)
    {
        b.ToTable(
            "TicketMessages",
            t =>
            {
                t.HasCheckConstraint("CK_TicketMessages_Sequence", "\"Sequence\" > 0");
                t.HasCheckConstraint("CK_TicketMessages_Content", "length(trim(\"Content\")) > 0");
            }
        );
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.Content).HasMaxLength(4000).IsRequired();

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(m => new { m.TicketId, m.Sequence }).IsUnique(); // deterministic order, no duplicate positions
        b.HasIndex(
                m => new
                {
                    m.TenantId,
                    m.TicketId,
                    m.SenderUserId,
                    m.ClientMessageId,
                },
                "IX_TicketMessages_ClientMessageId"
            )
            .IsUnique(); // explicit short name: PostgreSQL truncates identifiers over 63 chars // idempotent retries
    }
}
