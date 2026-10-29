using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OpsFlow.Domain.Invitations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Abstractions;

public interface IOpsFlowDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Invitation> Invitations { get; }
    ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>True only when the exception is a unique-constraint violation of exactly this constraint (PostgreSQL SQLSTATE 23505).</summary>
    bool IsUniqueViolation(DbUpdateException exception, UniqueConstraintKind constraint);
}
