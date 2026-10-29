using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Infrastructure.Persistence;

public sealed class OpsFlowDbContext(DbContextOptions<OpsFlowDbContext> options, ITenantContext tenantContext)
    : IdentityUserContext<ApplicationUser, Guid>(options), IOpsFlowDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    // Evaluated per query by EF; null (no tenant resolved) matches nothing, so tenant data fails closed.
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(OpsFlowDbContext).Assembly);

        builder.Entity<Tenant>().HasQueryFilter(t => t.Id == CurrentTenantId);
        builder.Entity<Membership>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        builder.Entity<Invitation>().HasQueryFilter(i => i.TenantId == CurrentTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantBoundaries();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantBoundaries();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Write protection: tenant data may only be written inside a tenant scope and only for that tenant.</summary>
    private void EnforceTenantBoundaries()
    {
        var current = tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var owner = entry.Entity switch
            {
                ITenantOwned o => o.TenantId,
                Tenant t => t.Id,
                _ => (Guid?)null,
            };
            if (owner is null)
            {
                continue;
            }

            if (current is null || owner != current)
            {
                throw new TenantViolationException($"Write to {entry.Metadata.ClrType.Name} outside of its tenant scope was blocked.");
            }

            if (entry.State == EntityState.Modified && entry.Entity is ITenantOwned && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
            {
                throw new TenantViolationException($"Changing TenantId of {entry.Metadata.ClrType.Name} is not allowed.");
            }
        }
    }
}
