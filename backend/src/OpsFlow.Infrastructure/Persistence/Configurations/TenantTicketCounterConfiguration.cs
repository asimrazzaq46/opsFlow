using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TenantTicketCounterConfiguration : IEntityTypeConfiguration<TenantTicketCounter>
{
    public void Configure(EntityTypeBuilder<TenantTicketCounter> b)
    {
        b.ToTable("TenantTicketCounters", t => t.HasCheckConstraint("CK_TenantTicketCounters_LastNumber", "\"LastNumber\" >= 0"));
        b.HasKey(c => c.TenantId);
        b.Property(c => c.TenantId).ValueGeneratedNever();
        b.HasOne<Tenant>().WithOne().HasForeignKey<TenantTicketCounter>(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
