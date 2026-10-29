using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Infrastructure.Persistence;

// UPDATED: IdentityUserContext hata diya; ab plain DbContext + domain-owned User/session/token entities
public sealed class OpsFlowDbContext(DbContextOptions<OpsFlowDbContext> options, ITenantContext tenantContext)
    : DbContext(options), IOpsFlowDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserToken> UserTokens => Set<UserToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    // Evaluated per query by EF; null (no tenant resolved) matches nothing, so tenant data fails closed.
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpsFlowDbContext).Assembly);

        modelBuilder.Entity<Tenant>().HasQueryFilter(t => t.Id == CurrentTenantId);
        modelBuilder.Entity<Membership>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        modelBuilder.Entity<Invitation>().HasQueryFilter(i => i.TenantId == CurrentTenantId);
    }

    // Only the expected unique constraint is recognised; every other DbUpdateException must propagate.
    public bool IsUniqueViolation(DbUpdateException exception, UniqueConstraintKind constraint)
    {
        var (postgresIndex, sqliteColumn) = constraint switch
        {
            UniqueConstraintKind.UserEmail => ("IX_Users_NormalizedEmail", "Users.NormalizedEmail"),
            UniqueConstraintKind.TenantSlug => ("IX_Tenants_Slug", "Tenants.Slug"),
            UniqueConstraintKind.MembershipUser => ("IX_Memberships_TenantId_UserId", "Memberships.UserId"),
            UniqueConstraintKind.OpenInvitation => ("IX_Invitations_TenantId_NormalizedEmail", "Invitations.NormalizedEmail"),
            _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
        };

        return exception.InnerException switch
        {
            PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation && pg.ConstraintName == postgresIndex,
            // shortcut: SQLite only backs the fast test suite and reports columns, not index names; production is PostgreSQL.
            { } inner when Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite" =>
                inner.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal) && inner.Message.Contains(sqliteColumn, StringComparison.Ordinal),
            _ => false,
        };
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Database.BeginTransactionAsync(cancellationToken);

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
