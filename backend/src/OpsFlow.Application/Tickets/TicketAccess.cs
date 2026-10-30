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
public sealed class TicketAccess(ActorAccess actorAccess, ICurrentUser currentUser, ITenantContext tenantContext, IOpsFlowDbContext db)
{
    public async Task<TicketActor> ResolveAsync(CancellationToken ct)
    {
        var role = await actorAccess.RequireActiveRoleAsync(ct);
        return new TicketActor(currentUser.UserId, tenantContext.MembershipId!.Value, role);
    }

    /// <summary>Authorized mutation transaction (see <see cref="ActorAccess.BeginAuthorizedAsync"/>); extra memberships (e.g. assignees) are locked and re-validated too.</summary>
    public async Task<(IDbContextTransaction Transaction, TicketActor Actor)> BeginAuthorizedAsync(IReadOnlyCollection<Guid>? alsoLock, CancellationToken ct)
    {
        var (transaction, role) = await actorAccess.BeginAuthorizedAsync(alsoLock, ct);
        try
        {
            return (transaction, new TicketActor(currentUser.UserId, tenantContext.MembershipId!.Value, role));
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
        db.TicketAssignments.Where(a => a.UnassignedAtUtc == null
            && db.Memberships.Any(m => m.Id == a.MembershipId && m.Role == TenantRole.Agent && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)));

    public IQueryable<Ticket> Visible(TicketActor actor) => actor.Role switch
    {
        TenantRole.TenantOwner or TenantRole.Manager => db.Tickets,
        TenantRole.Agent => db.Tickets.Where(t => t.CreatedByUserId == actor.UserId
            || db.TicketAssignments.Any(a => a.TicketId == t.Id && a.MembershipId == actor.MembershipId && a.UnassignedAtUtc == null)),
        _ => db.Tickets.Where(t => t.CustomerId != null && db.Customers.Any(c => c.Id == t.CustomerId && c.UserId == actor.UserId)),
    };

    public Task<bool> IsActiveAssigneeAsync(Guid ticketId, Guid membershipId, CancellationToken ct) =>
        db.TicketAssignments.AnyAsync(a => a.TicketId == ticketId && a.MembershipId == membershipId && a.UnassignedAtUtc == null, ct);

    /// <summary>Every agent must be an ACTIVE Agent membership of THIS tenant whose user account is active.</summary>
    public async Task<List<Guid>> RequireEligibleAgentsAsync(IReadOnlyCollection<Guid> membershipIds, CancellationToken ct)
    {
        var distinct = membershipIds.Distinct().ToList();
        var eligible = await db.Memberships.AsNoTracking()
            .Where(m => distinct.Contains(m.Id) && m.Role == TenantRole.Agent && m.Status == MembershipStatus.Active
                && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active))
            .Select(m => m.Id).ToListAsync(ct);
        return eligible.Count == distinct.Count && distinct.Count > 0
            ? eligible
            : throw AppException.BadRequest("Every assignee must be an active Agent of this organization.");
    }
}
