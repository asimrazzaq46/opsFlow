using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
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
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();
    public DbSet<TicketActivity> TicketActivities => Set<TicketActivity>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<TicketSlaCycle> TicketSlaCycles => Set<TicketSlaCycle>();
    public DbSet<TenantSettings> Settings => Set<TenantSettings>();
    public DbSet<TenantTicketCounter> TicketCounters => Set<TenantTicketCounter>();
    // UPDATED: Milestone 3 - chat, internal notes, attachments, notifications
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<Notification> Notifications => Set<Notification>();

    // Evaluated per query by EF; null (no tenant resolved) matches nothing, so tenant data fails closed.
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpsFlowDbContext).Assembly);

        modelBuilder.Entity<Tenant>().HasQueryFilter(t => t.Id == CurrentTenantId);
        modelBuilder.Entity<Membership>().HasQueryFilter(m => m.TenantId == CurrentTenantId);
        modelBuilder.Entity<Invitation>().HasQueryFilter(i => i.TenantId == CurrentTenantId);
        // UPDATED: Milestone 2 ke tenant-owned entities - har ek par automatic tenant filter (tenant na ho to koi row nahi)
        modelBuilder.Entity<Customer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketCategory>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Ticket>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketAssignment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketActivity>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SlaPolicy>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketSlaCycle>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantTicketCounter>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketMessage>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketNote>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketAttachment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Notification>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
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
            UniqueConstraintKind.CustomerEmail => ("IX_Customers_TenantId_NormalizedEmail", "Customers.NormalizedEmail"),
            UniqueConstraintKind.CustomerUser => ("IX_Customers_TenantId_UserId", "Customers.UserId"),
            UniqueConstraintKind.CategoryName => ("IX_TicketCategories_TenantId_NormalizedName", "TicketCategories.NormalizedName"),
            UniqueConstraintKind.SlaPolicyPriority => ("IX_SlaPolicies_TenantId_Priority", "SlaPolicies.Priority"),
            UniqueConstraintKind.OpenAssignment => ("IX_TicketAssignments_TicketId_MembershipId", "TicketAssignments.MembershipId"),
            UniqueConstraintKind.ActiveSlaCycle => ("IX_TicketSlaCycles_TicketId", "TicketSlaCycles.TicketId"),
            UniqueConstraintKind.ActivitySequence => ("IX_TicketActivities_TicketId_Sequence", "TicketActivities.Sequence"),
            UniqueConstraintKind.MessageClientId => ("IX_TicketMessages_ClientMessageId", "TicketMessages.ClientMessageId"),
            UniqueConstraintKind.NoteClientId => ("IX_TicketNotes_ClientNoteId", "TicketNotes.ClientNoteId"),
            UniqueConstraintKind.NotificationDedup => ("IX_Notifications_TenantId_UserId_DedupKey", "Notifications.DedupKey"),
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

    // UPDATED: concurrent ticket writers ka deadlock victim 500 nahi, conflict hai
    public bool IsDeadlock(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => Database.BeginTransactionAsync(cancellationToken);

    // UPDATED: actor/assignee rows par FOR SHARE lock (transaction ke end tak) - authorization commit boundary tak valid rahe
    public async Task LockMembershipsAsync(IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken = default)
    {
        if (!Database.IsNpgsql() || membershipIds.Count == 0 || tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        var ids = membershipIds.Distinct().Order().ToArray(); // fixed order: two lockers can never deadlock on each other
        await Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Memberships\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR SHARE", cancellationToken);
        await Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" IN (SELECT \"UserId\" FROM \"Memberships\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = ANY({ids})) ORDER BY \"Id\" FOR SHARE", cancellationToken);
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
