using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> b)
    {
        b.ToTable(
            "SlaPolicies",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_SlaPolicies_Priority",
                    "\"Priority\" IN ('Low','Medium','High','Urgent')"
                );
                t.HasCheckConstraint(
                    "CK_SlaPolicies_Targets",
                    "\"ResponseMinutes\" BETWEEN 1 AND 525600 AND \"ResolutionMinutes\" BETWEEN 1 AND 525600 AND \"ResolutionMinutes\" >= \"ResponseMinutes\""
                );
            }
        );
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.HasAlternateKey(p => new { p.TenantId, p.Id });
        b.Property(p => p.Priority).HasConversion<string>().HasMaxLength(20);

        b.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => new { p.TenantId, p.Priority }).IsUnique().HasFilter("\"IsActive\""); // one ACTIVE policy per priority
    }
}
