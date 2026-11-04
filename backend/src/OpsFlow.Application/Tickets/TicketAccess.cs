using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Tickets;

/// <summary>The caller as seen by the DATABASE right now (current role, active membership AND active user) - never the JWT.</summary>
public sealed record TicketActor(Guid UserId, Guid MembershipId, TenantRole Role)
{
    public bool IsManager => Role is TenantRole.TenantOwner or TenantRole.Manager;

    public bool IsCustomer => Role == TenantRole.Customer;
}

/// <summary>
/// Who may see which tickets. Authorization is part of the QUERY (never filter-after-fetch):
/// Owner/Manager see the whole tenant; an Agent sees tickets assigned to them or created by them; a Customer sees only tickets
/// of the contact that is explicitly linked to their user account.
/// </summary>
public sealed class TicketAccess(
    ActorAccess actorAccess,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    IOpsFlowDbContext db
)
{
    /// <summary>Throwing form for callers shared with GraphQL; request handlers use <see cref="TryResolveAsync"/>.</summary>
    public async Task<TicketActor> ResolveAsync(CancellationToken ct) =>
        await TryResolveAsync(ct)
        ?? throw AppException.Forbidden("Your access to this organization is no longer valid.");

    /// <returns>The caller as the database sees them now, or null when their access is no longer valid.</returns>
    public async Task<TicketActor?> TryResolveAsync(CancellationToken ct) =>
        await actorAccess.GetActiveRoleAsync(ct) is { } role
            ? new TicketActor(currentUser.UserId, tenantContext.MembershipId!.Value, role)
            : null;

    /// <summary>
    /// Authorized mutation transaction (see <see cref="ActorAccess.TryBeginAuthorizedAsync"/>); extra memberships (e.g. assignees) are locked
    /// and re-validated too. Null when the actor's access is no longer valid (nothing was written).
    /// </summary>
    public async Task<(IDbContextTransaction Transaction, TicketActor Actor)?> TryBeginAuthorizedAsync(
        IReadOnlyCollection<Guid>? alsoLock,
        CancellationToken ct
    )
    {
        if (await actorAccess.TryBeginAuthorizedAsync(alsoLock, ct) is not { } begun)
        {
            return null;
        }

        var (transaction, role) = begun;
        try
        {
            return (
                transaction,
                new TicketActor(currentUser.UserId, tenantContext.MembershipId!.Value, role)
            );
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>Throwing form of <see cref="TryBeginAuthorizedAsync"/> for callers that have not been migrated.</summary>
    public async Task<(IDbContextTransaction Transaction, TicketActor Actor)> BeginAuthorizedAsync(
        IReadOnlyCollection<Guid>? alsoLock,
        CancellationToken ct
    )
    {
        var (transaction, role) = await actorAccess.BeginAuthorizedAsync(alsoLock, ct);
        try
        {
            return (
                transaction,
                new TicketActor(currentUser.UserId, tenantContext.MembershipId!.Value, role)
            );
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// EFFECTIVE assignments: the row is open AND the agent is still eligible (Agent role, active membership, active user). Suspended,
    /// deactivated or re-roled agents keep their history row but no longer count as ticket owners.
    /// </summary>
    public IQueryable<TicketAssignment> EffectiveAssignments() =>
        db.TicketAssignments.Where(a =>
            a.UnassignedAtUtc == null
            && db.Memberships.Any(m =>
                m.Id == a.MembershipId
                && m.Role == TenantRole.Agent
                && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
            )
        );

    public IQueryable<Ticket> Visible(TicketActor actor) =>
        actor.Role switch
        {
            TenantRole.TenantOwner or TenantRole.Manager => db.Tickets,
            TenantRole.Agent => db.Tickets.Where(t =>
                t.CreatedByUserId == actor.UserId
                || db.TicketAssignments.Any(a =>
                    a.TicketId == t.Id
                    && a.MembershipId == actor.MembershipId
                    && a.UnassignedAtUtc == null
                )
            ),
            _ => db.Tickets.Where(t =>
                t.CustomerId != null
                && db.Customers.Any(c => c.Id == t.CustomerId && c.UserId == actor.UserId)
            ),
        };

    public Task<bool> IsActiveAssigneeAsync(
        Guid ticketId,
        Guid membershipId,
        CancellationToken ct
    ) =>
        db.TicketAssignments.AnyAsync(
            a =>
                a.TicketId == ticketId
                && a.MembershipId == membershipId
                && a.UnassignedAtUtc == null,
            ct
        );

    /// <summary>
    /// Next per-ticket sequence shared by messages and notes. The ExecuteUpdate holds the ticket row lock until commit, so concurrent
    /// writers serialize and no sequence is handed out twice; the ticket's Version/ActivitySequence are not touched.
    /// </summary>
    public async Task<int> NextMessageSequenceAsync(Guid ticketId, CancellationToken ct)
    {
        var rows = await db
            .Tickets.Where(t => t.Id == ticketId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.MessageSequence, t => t.MessageSequence + 1),
                ct
            );
        return rows == 1
            ? await db
                .Tickets.AsNoTracking()
                .Where(t => t.Id == ticketId)
                .Select(t => t.MessageSequence)
                .SingleAsync(ct)
            : throw AppException.NotFound("Ticket not found.");
    }

    /// <summary>
    /// Takes the ticket row lock until commit without changing any value. Child-task writes and agent unassignment serialize on it,
    /// so "agent removed from the ticket" and "task given to that agent" can never both commit.
    /// </summary>
    /// <returns>False when the ticket does not exist in this tenant.</returns>
    public async Task<bool> TryLockTicketAsync(Guid ticketId, CancellationToken ct) =>
        await db
            .Tickets.Where(t => t.Id == ticketId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ActivitySequence, t => t.ActivitySequence),
                ct
            ) == 1;

    /// <returns>The distinct ids when EVERY one is eligible (and at least one was given); otherwise null.</returns>
    public async Task<List<Guid>?> GetEligibleAgentsAsync(
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken ct
    )
    {
        var distinct = membershipIds.Distinct().ToList();
        var eligible = await db
            .Memberships.AsNoTracking()
            .Where(m =>
                distinct.Contains(m.Id)
                && m.Role == TenantRole.Agent
                && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
            )
            .Select(m => m.Id)
            .ToListAsync(ct);
        return eligible.Count == distinct.Count && distinct.Count > 0 ? eligible : null;
    }
}
