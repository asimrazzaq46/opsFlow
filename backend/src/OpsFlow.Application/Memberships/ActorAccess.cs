using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>A 403 answer for a caller that lacks permission (title is client-safe).</summary>
    public static ObjectResult Forbidden(string title) =>
        new(new ProblemDetails { Title = title }) { StatusCode = StatusCodes.Status403Forbidden };

    /// <summary>The 403 answer for an actor whose membership or account is no longer active.</summary>
    public static ObjectResult AccessNoLongerValid() =>
        Forbidden("Your access to this organization is no longer valid.");

    /// <summary>Throws Forbidden for callers shared with GraphQL; request handlers use <see cref="GetActiveRoleAsync"/>.</summary>
    public async Task<TenantRole> RequireActiveRoleAsync(CancellationToken ct) =>
        await GetActiveRoleAsync(ct)
        ?? throw AppException.Forbidden("Your access to this organization is no longer valid.");

    /// <returns>Null for an active Owner; otherwise the 403 to answer with.</returns>
    public async Task<ObjectResult?> CheckOwnerAsync(CancellationToken ct) =>
        await GetActiveRoleAsync(ct) switch
        {
            null => AccessNoLongerValid(),
            TenantRole.TenantOwner => null,
            _ => Forbidden("Only an active Owner can change memberships."),
        };

    /// <returns>The actor's CURRENT role, or null when the membership or the user account is no longer active.</returns>
    public async Task<TenantRole?> GetActiveRoleAsync(CancellationToken ct)
    {
        var actorId = tenantContext.MembershipId;
        TenantRole? role = actorId is null
            ? null
            : await db
                .Memberships.Where(m =>
                    m.Id == actorId
                    && m.Status == MembershipStatus.Active
                    && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
                )
                .Select(m => (TenantRole?)m.Role)
                .SingleOrDefaultAsync(ct);
        return role;
    }

    /// <summary>
    /// Starts the transaction of a MUTATION: locks the actor's (and the given extra) membership/user rows first, THEN re-reads the actor's
    /// current role. Until the caller commits or disposes, no demotion/suspension/deactivation of those rows can commit, so authorization
    /// is valid at the commit boundary. The caller commits.
    /// </summary>
    public async Task<(
        IDbContextTransaction Transaction,
        TenantRole Role
    )?> TryBeginAuthorizedAsync(IReadOnlyCollection<Guid>? alsoLock, CancellationToken ct)
    {
        if (tenantContext.MembershipId is not { } actorId)
        {
            return null;
        }

        var transaction = await db.BeginTransactionAsync(ct);
        try
        {
            await db.LockMembershipsAsync([actorId, .. alsoLock ?? []], ct);
            if (await GetActiveRoleAsync(ct) is { } role)
            {
                return (transaction, role);
            }

            await transaction.DisposeAsync(); // rejected: nothing was written
            return null;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>Throwing form of <see cref="TryBeginAuthorizedAsync"/> for callers that have not been migrated.</summary>
    public async Task<(IDbContextTransaction Transaction, TenantRole Role)> BeginAuthorizedAsync(
        IReadOnlyCollection<Guid>? alsoLock,
        CancellationToken ct
    ) =>
        await TryBeginAuthorizedAsync(alsoLock, ct)
        ?? throw AppException.Forbidden("Your access to this organization is no longer valid.");

    public async Task RequireManagerAsync(string forbiddenMessage, CancellationToken ct)
    {
        if (await RequireActiveRoleAsync(ct) is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            throw AppException.Forbidden(forbiddenMessage);
        }
    }
}

public static class MembershipQueries
{
    /// <summary>Owners that actually count: ACTIVE membership AND active user account (a deactivated Owner is not usable).</summary>
    public static IQueryable<Membership> UsableOwners(
        this IOpsFlowDbContext db,
        bool allTenants = false
    ) =>
        (allTenants ? db.Memberships.IgnoreQueryFilters() : db.Memberships).Where(m =>
            m.Role == TenantRole.TenantOwner
            && m.Status == MembershipStatus.Active
            && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
        );
}
