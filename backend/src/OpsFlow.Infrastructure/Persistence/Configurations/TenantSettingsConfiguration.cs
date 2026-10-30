using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class TenantSettingsConfiguration : IEntityTypeConfiguration<TenantSettings>
{
    public void Configure(EntityTypeBuilder<TenantSettings> b)
    {
        b.ToTable("TenantSettings", t => t.HasCheckConstraint("CK_TenantSettings_Prefix", "length(\"TicketNumberPrefix\") > 0"));
        b.HasKey(s => s.TenantId);
        b.Property(s => s.TenantId).ValueGeneratedNever();
        b.Property(s => s.TicketNumberPrefix).HasMaxLength(10).IsRequired();
        b.HasOne<Tenant>().WithOne().HasForeignKey<TenantSettings>(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
