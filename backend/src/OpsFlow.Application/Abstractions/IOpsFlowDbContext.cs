using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Abstractions;

public interface IOpsFlowDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<User> Users { get; }
    DbSet<UserToken> UserTokens { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Customer> Customers { get; }
    DbSet<TicketCategory> TicketCategories { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<TicketAssignment> TicketAssignments { get; }
    DbSet<TicketActivity> TicketActivities { get; }
    DbSet<SlaPolicy> SlaPolicies { get; }
    DbSet<TicketSlaCycle> TicketSlaCycles { get; }
    DbSet<TenantSettings> Settings { get; }
    DbSet<TenantTicketCounter> TicketCounters { get; }
    DbSet<TicketMessage> TicketMessages { get; }
    DbSet<TicketNote> TicketNotes { get; }
    DbSet<TicketAttachment> TicketAttachments { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<OutboxMessage> OutboxMessages { get; } // UPDATED: Milestone 4 - transactional outbox
    ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Explicit transaction for operations that must change several rows/tenants atomically.</summary>
    Task<IDbContextTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Takes a shared row lock (PostgreSQL FOR SHARE) on these memberships of the CURRENT tenant and their user accounts, held until
    /// the surrounding transaction ends. A concurrent demotion/suspension/deactivation then waits for the commit instead of slipping in
    /// between an authorization check and the write. No-op on providers that serialize writers anyway (SQLite).
    /// </summary>
    Task LockMembershipsAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken = default
    );
    /// <summary>True on PostgreSQL (production provider); false for the SQLite test provider where interval arithmetic cannot be translated.</summary>
    bool IsPostgreSql { get; }


    /// <summary>True only when the exception is a unique-constraint violation of exactly this constraint (PostgreSQL SQLSTATE 23505).</summary>
    bool IsUniqueViolation(DbUpdateException exception, UniqueConstraintKind constraint);

    /// <summary>True when PostgreSQL aborted the transaction as a deadlock victim (SQLSTATE 40P01): the other writer won, so callers report "changed concurrently".</summary>
    bool IsDeadlock(Exception exception);
}
