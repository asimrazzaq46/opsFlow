using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Multi-agent assignments. Only Owners/Managers may change them, and the actor's CURRENT authorization must still hold at commit:
/// the tenant OwnerChangeStamp is read first and rotated in the same transaction (same protocol as membership/invitation changes),
/// so an actor demoted/suspended mid-request cannot commit. Assignment rows are closed, never deleted, so history is preserved.
/// </summary>
public sealed partial class TicketAssignmentService(IOpsFlowDbContext db, TicketAccess access, TicketService tickets, TimeProvider clock, ILogger<TicketAssignmentService> logger)
{
    private const int MaxAttempts = 3;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<TicketDetailDto> AssignAsync(Guid ticketId, IReadOnlyCollection<Guid> membershipIds, int? expectedVersion, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var tenant = await db.Tenants.SingleAsync(ct); // stamp first, then the actor's CURRENT authorization
            var actor = await access.ResolveAsync(ct);
            var ticket = await LoadAsync(actor, ticketId, expectedVersion, ct);
            if (TicketWorkflow.IsFinished(ticket.Status))
            {
                throw AppException.Conflict("Reopen the ticket before assigning agents.");
            }

            var agents = await access.RequireEligibleAgentsAsync(membershipIds, ct);
            if (await db.TicketAssignments.AnyAsync(a => a.TicketId == ticketId && a.UnassignedAtUtc == null && agents.Contains(a.MembershipId), ct))
            {
                throw AppException.Conflict("An agent is already assigned to this ticket.");
            }

            var now = Now;
            foreach (var membershipId in agents)
            {
                db.TicketAssignments.Add(TicketAssignment.Create(ticket.TenantId, ticketId, membershipId, actor.MembershipId, now));
                db.TicketActivities.Add(ticket.Record(TicketActivityType.AgentAssigned, actor.UserId, now, relatedId: membershipId));
            }

            if (ticket.Status == TicketStatus.Open)
            {
                ticket.SetStatus(TicketStatus.Assigned, now);
                db.TicketActivities.Add(ticket.Record(TicketActivityType.StatusChanged, actor.UserId, now, TicketStatus.Open.ToString(), TicketStatus.Assigned.ToString()));
            }
            else
            {
                ticket.Touch(now);
            }

            tenant.RotateOwnerChangeStamp();
            try
            {
                await db.SaveChangesAsync(ct);
                LogAssigned(logger, ticketId, agents.Count);
                return await tickets.GetAsync(ticketId, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // lost a race: retry re-reads the ticket, the assignments and the actor's authorization
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
            {
                db.ChangeTracker.Clear(); // same race, reported by the timeline's unique sequence first
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.OpenAssignment))
            {
                throw AppException.Conflict("An agent is already assigned to this ticket.");
            }
        }

        throw AppException.Conflict("The ticket was changed concurrently.", "Please retry.");
    }

    /// <summary>Removes one agent. The last assignee cannot be removed while the ticket's status requires an owner.</summary>
    public async Task<TicketDetailDto> UnassignAsync(Guid ticketId, Guid membershipId, int? expectedVersion, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var tenant = await db.Tenants.SingleAsync(ct);
            var actor = await access.ResolveAsync(ct);
            var ticket = await LoadAsync(actor, ticketId, expectedVersion, ct);
            if (ticket.Status == TicketStatus.Closed)
            {
                throw AppException.Conflict("A closed ticket cannot be changed.");
            }

            var assignment = await db.TicketAssignments.SingleOrDefaultAsync(a => a.TicketId == ticketId && a.MembershipId == membershipId && a.UnassignedAtUtc == null, ct)
                ?? throw AppException.NotFound("The agent is not assigned to this ticket.");
            // UPDATED: sirf ELIGIBLE agents ginay jate hain: aakhri eligible assignee hat nahi sakta, lekin ineligible (suspended/deactivated) agent
            // ko hamesha hata sakte hain jab doosra eligible agent maujood ho (pehle naya agent assign karein)
            var remainingEligible = await access.EffectiveAssignments().CountAsync(a => a.TicketId == ticketId && a.Id != assignment.Id, ct);
            if (remainingEligible == 0 && TicketWorkflow.RequiresAssignee(ticket.Status))
            {
                throw AppException.Conflict("The last eligible assignee cannot be removed while the ticket is " + ticket.Status + ".", "Assign another active agent first.");
            }

            var now = Now;
            assignment.Unassign(actor.MembershipId, now);
            db.TicketActivities.Add(ticket.Record(TicketActivityType.AgentUnassigned, actor.UserId, now, relatedId: membershipId));
            ticket.Touch(now);
            tenant.RotateOwnerChangeStamp();
            try
            {
                await db.SaveChangesAsync(ct);
                return await tickets.GetAsync(ticketId, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
            {
                db.ChangeTracker.Clear();
            }
        }

        throw AppException.Conflict("The ticket was changed concurrently.", "Please retry.");
    }

    /// <summary>Current assignees, or the complete history (including removed agents). Staff only.</summary>
    public async Task<IReadOnlyList<AssignmentHistoryDto>> ListAsync(Guid ticketId, bool includeHistory, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Customers cannot view assignments.");
        }

        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw AppException.NotFound("Ticket not found.");
        }

        return await (from a in db.TicketAssignments.AsNoTracking()
                      join m in db.Memberships on a.MembershipId equals m.Id
                      join u in db.Users on m.UserId equals u.Id
                      where a.TicketId == ticketId && (includeHistory || a.UnassignedAtUtc == null)
                      orderby a.AssignedAtUtc, a.Id
                      select new AssignmentHistoryDto(a.Id, m.Id, u.Id, u.DisplayName, a.AssignedAtUtc, a.AssignedByMembershipId, a.UnassignedAtUtc, a.UnassignedByMembershipId, a.UnassignedAtUtc == null))
            .ToListAsync(ct);
    }

    private async Task<Ticket> LoadAsync(TicketActor actor, Guid ticketId, int? expectedVersion, CancellationToken ct)
    {
        if (!actor.IsManager)
        {
            throw AppException.Forbidden("Only Owners and Managers can change assignments.");
        }

        var ticket = await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Ticket not found.");
        return expectedVersion is { } v && v != ticket.Version
            ? throw AppException.Conflict("The ticket was changed by someone else.", "Reload the ticket and retry.")
            : ticket;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId}: {Count} agent(s) assigned")]
    private static partial void LogAssigned(ILogger logger, Guid ticketId, int count);
}
