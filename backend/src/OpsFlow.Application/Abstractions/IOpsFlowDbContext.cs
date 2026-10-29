using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;
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
    ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Explicit transaction for operations that must change several rows/tenants atomically.</summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>True only when the exception is a unique-constraint violation of exactly this constraint (PostgreSQL SQLSTATE 23505).</summary>
    bool IsUniqueViolation(DbUpdateException exception, UniqueConstraintKind constraint);
}
