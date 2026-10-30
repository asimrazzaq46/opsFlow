using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("OutboxMessages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.EventType).HasMaxLength(60).IsRequired();
        b.Property(m => m.Payload).HasMaxLength(2000).IsRequired();
        b.Property(m => m.DedupKey).HasMaxLength(150).IsRequired();
        b.Property(m => m.CorrelationId).HasMaxLength(64);
        b.Property(m => m.LastError).HasMaxLength(500);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);

        b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(m => new { m.TenantId, m.DedupKey }).IsUnique(); // a retried business operation cannot enqueue the same event twice
        // dispatcher claim query: only unfinished rows are indexed
        b.HasIndex(m => new { m.NextAttemptAtUtc, m.ClaimedUntilUtc }).HasFilter("\"Status\" IN ('Pending','RetryScheduled','Claimed')");
        b.HasIndex(m => m.ProcessedAtUtc).HasFilter("\"Status\" = 'Completed'"); // retention cleanup
        b.HasIndex(m => m.CreatedAtUtc).HasFilter("\"Status\" = 'DeadLetter'"); // dead-letter visibility
    }
}
