using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TicketNoteConfiguration : IEntityTypeConfiguration<TicketNote>
{
    public void Configure(EntityTypeBuilder<TicketNote> b)
    {
        b.ToTable("TicketNotes", t =>
        {
            t.HasCheckConstraint("CK_TicketNotes_Sequence", "\"Sequence\" > 0");
            t.HasCheckConstraint("CK_TicketNotes_Content", "length(trim(\"Content\")) > 0");
        });
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).ValueGeneratedNever();
        b.Property(n => n.Content).HasMaxLength(4000).IsRequired();

        b.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ticket>().WithMany().HasForeignKey(n => new { n.TenantId, n.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(n => n.AuthorUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(n => new { n.TicketId, n.Sequence }).IsUnique();
        b.HasIndex(n => new { n.TenantId, n.TicketId, n.AuthorUserId, n.ClientNoteId }, "IX_TicketNotes_ClientNoteId").IsUnique();
    }
}
