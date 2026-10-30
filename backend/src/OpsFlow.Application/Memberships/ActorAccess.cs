using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Memberships;

/// <summary>
/// The CURRENT authorization of the request's actor, read from the database (never from the request-start snapshot):
/// the membership must be active AND the user account must be active. Callers read the tenant OwnerChangeStamp first and
/// rotate it on commit, so an authorization change that lands in between makes the commit fail and the retry re-check this.
/// </summary>
public sealed class ActorAccess(IOpsFlowDbContext db, ITenantContext tenantContext)
{
    public Guid TenantId => tenantContext.TenantId!.Value;

    public async Task<TenantRole> RequireActiveRoleAsync(CancellationToken ct)
    {
        var actorId = tenantContext.MembershipId;
        TenantRole? role = actorId is null
            ? null
            : await db.Memberships
                .Where(m => m.Id == actorId && m.Status == MembershipStatus.Active && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active))
                .Select(m => (TenantRole?)m.Role)
                .SingleOrDefaultAsync(ct);
        return role ?? throw AppException.Forbidden("Your access to this organization is no longer valid.");
    }

    /// <summary>
    /// Starts the transaction of a MUTATION: locks the actor's (and the given extra) membership/user rows first, THEN re-reads the actor's
    /// current role. Until the caller commits or disposes, no demotion/suspension/deactivation of those rows can commit, so authorization
    /// is valid at the commit boundary. The caller commits.
    /// </summary>
    public async Task<(IDbContextTransaction Transaction, TenantRole Role)> BeginAuthorizedAsync(IReadOnlyCollection<Guid>? alsoLock, CancellationToken ct)
    {
        var actorId = tenantContext.MembershipId ?? throw AppException.Forbidden("Your access to this organization is no longer valid.");
        var transaction = await db.BeginTransactionAsync(ct);
        try
        {
            await db.LockMembershipsAsync([actorId, .. alsoLock ?? []], ct);
            return (transaction, await RequireActiveRoleAsync(ct));
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task RequireManagerAsync(string forbiddenMessage, CancellationToken ct)
    {
        if (await RequireActiveRoleAsync(ct) is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            throw AppException.Forbidden(forbiddenMessage);
        }
    }

    /// <summary>Owner/Manager-only mutation: an authorized transaction that is rejected (and rolled back) for everybody else.</summary>
    public async Task<IDbContextTransaction> BeginManagerAsync(string forbiddenMessage, CancellationToken ct)
    {
        var (transaction, role) = await BeginAuthorizedAsync(null, ct);
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            await transaction.DisposeAsync();
            throw AppException.Forbidden(forbiddenMessage);
        }

        return transaction;
    }

    public async Task RequireOwnerAsync(CancellationToken ct)
    {
        if (await RequireActiveRoleAsync(ct) != TenantRole.TenantOwner)
        {
            throw AppException.Forbidden("Only an active Owner can change memberships.");
        }
    }
}

public static class MembershipQueries
{
    /// <summary>Owners that actually count: ACTIVE membership AND active user account (a deactivated Owner is not usable).</summary>
    public static IQueryable<Membership> UsableOwners(this IOpsFlowDbContext db, bool allTenants = false) =>
        (allTenants ? db.Memberships.IgnoreQueryFilters() : db.Memberships)
            .Where(m => m.Role == TenantRole.TenantOwner && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active));
}
