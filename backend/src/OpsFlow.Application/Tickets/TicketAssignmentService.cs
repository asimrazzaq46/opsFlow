using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Multi-agent assignments. Only Owners/Managers may change them, and the actor's CURRENT authorization must still hold at commit:
/// the tenant OwnerChangeStamp is read first and rotated in the same transaction (same protocol as membership/invitation changes),
/// so an actor demoted/suspended mid-request cannot commit. Assignment rows are closed, never deleted, so history is preserved.
/// </summary>
public sealed partial class TicketAssignmentService(
    IOpsFlowDbContext db,
    TicketAccess access,
    TicketService tickets,
    NotificationService notifications,
    TimeProvider clock,
    ILogger<TicketAssignmentService> logger
)
{
    private const int MaxAttempts = 3;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<TicketDetailDto> AssignAsync(
        Guid ticketId,
        IReadOnlyCollection<Guid> membershipIds,
        int? expectedVersion,
        CancellationToken ct
    )
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
            if (
                await db.TicketAssignments.AnyAsync(
                    a =>
                        a.TicketId == ticketId
                        && a.UnassignedAtUtc == null
                        && agents.Contains(a.MembershipId),
                    ct
                )
            )
            {
                throw AppException.Conflict("An agent is already assigned to this ticket.");
            }

            var now = Now;
            foreach (var membershipId in agents)
            {
                db.TicketAssignments.Add(
                    TicketAssignment.Create(
                        ticket.TenantId,
                        ticketId,
                        membershipId,
                        actor.MembershipId,
                        now
                    )
                );
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.AgentAssigned,
                        actor.UserId,
                        now,
                        relatedId: membershipId
                    )
                );
            }

            if (ticket.Status == TicketStatus.Open)
            {
                ticket.SetStatus(TicketStatus.Assigned, now);
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.StatusChanged,
                        actor.UserId,
                        now,
                        TicketStatus.Open.ToString(),
                        TicketStatus.Assigned.ToString()
                    )
                );
            }
            else
            {
                ticket.Touch(now);
            }

            // New assignees are told directly; the customer only learns about Open -> Assigned, never who is staffing the ticket.
            var agentUsers = await db
                .Memberships.AsNoTracking()
                .Where(m => agents.Contains(m.Id))
                .Select(m => m.UserId)
                .ToListAsync(ct);
            await notifications.StageAsync(
                ticket,
                NotificationType.TicketAssigned,
                actor.UserId,
                $"assigned:{ticketId}:{ticket.Version}",
                toStaff: false,
                toCustomer: false,
                alsoUsers: agentUsers,
                ct: ct
            );
            if (ticket.Status == TicketStatus.Assigned)
            {
                await notifications.StageAsync(
                    ticket,
                    NotificationType.TicketStatusChanged,
                    actor.UserId,
                    $"status:{ticketId}:{ticket.Version}",
                    toStaff: false,
                    toCustomer: true,
                    detail: nameof(TicketStatus.Assigned),
                    ct: ct
                );
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
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence)
                    || db.IsUniqueViolation(ex, UniqueConstraintKind.NotificationDedup)
                    || db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup)
                ) // a same-version race can also surface as a notification/outbox dedup violation
            {
                db.ChangeTracker.Clear(); // same race, reported by the timeline's unique sequence first
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.OpenAssignment))
            {
                throw AppException.Conflict("An agent is already assigned to this ticket.");
            }
        }

        throw AppException.Conflict("The ticket was changed concurrently.", "Please retry.");
    }

    /// <summary>Removes one agent. The last assignee cannot be removed while the ticket's status requires an owner.</summary>
    public async Task<TicketDetailDto> UnassignAsync(
        Guid ticketId,
        Guid membershipId,
        int? expectedVersion,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // One transaction per attempt: the ticket row lock serializes this with child-task creation and reassignment.
            await using var transaction = await db.BeginTransactionAsync(ct);
            var tenant = await db.Tenants.SingleAsync(ct);
            var actor = await access.ResolveAsync(ct);
            if (!actor.IsManager)
            {
                throw AppException.Forbidden("Only Owners and Managers can change assignments.");
            }

            await access.LockTicketAsync(ticketId, ct);
            var ticket = await LoadAsync(actor, ticketId, expectedVersion, ct);
            if (ticket.Status == TicketStatus.Closed)
            {
                throw AppException.Conflict("A closed ticket cannot be changed.");
            }

            var assignment =
                await db.TicketAssignments.SingleOrDefaultAsync(
                    a =>
                        a.TicketId == ticketId
                        && a.MembershipId == membershipId
                        && a.UnassignedAtUtc == null,
                    ct
                ) ?? throw AppException.NotFound("The agent is not assigned to this ticket.");
            // Only ELIGIBLE agents count: the last eligible assignee cannot be removed, but an ineligible (suspended/deactivated)
            // agent can always be removed once another eligible agent is assigned.
            var remainingEligible = await access
                .EffectiveAssignments()
                .CountAsync(a => a.TicketId == ticketId && a.Id != assignment.Id, ct);
            if (remainingEligible == 0 && TicketWorkflow.RequiresAssignee(ticket.Status))
            {
                throw AppException.Conflict(
                    "The last eligible assignee cannot be removed while the ticket is "
                        + ticket.Status
                        + ".",
                    "Assign another active agent first."
                );
            }

            // An agent who still owns unfinished child tasks cannot be unassigned (also not while Resolved, which would leave
            // orphaned tasks after a reopen); a manager reassigns the tasks first. Membership revocation is never blocked by this.
            if (
                await db.TicketTasks.AnyAsync(
                    t =>
                        t.TicketId == ticketId
                        && t.AssigneeMembershipId == membershipId
                        && t.Status != TicketTaskStatus.Done,
                    ct
                )
            )
            {
                throw AppException
                    .Conflict(
                        "The agent still owns unfinished child tasks on this ticket.",
                        "Reassign those tasks to another assigned agent first."
                    )
                    .WithCode("TICKET_AGENT_HAS_OPEN_TASKS");
            }

            var now = Now;
            assignment.Unassign(actor.MembershipId, now);
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.AgentUnassigned,
                    actor.UserId,
                    now,
                    relatedId: membershipId
                )
            );
            ticket.Touch(now);
            var removedUser = await db
                .Memberships.AsNoTracking()
                .Where(m => m.Id == membershipId)
                .Select(m => m.UserId)
                .SingleAsync(ct);
            await notifications.StageAsync(
                ticket,
                NotificationType.TicketUnassigned,
                actor.UserId,
                $"unassigned:{ticketId}:{ticket.Version}",
                toStaff: false,
                toCustomer: false,
                alsoUsers: [removedUser],
                ct: ct
            );
            tenant.RotateOwnerChangeStamp();
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return await tickets.GetAsync(ticketId, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence)
                    || db.IsUniqueViolation(ex, UniqueConstraintKind.NotificationDedup)
                    || db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup)
                ) // a same-version race can also surface as a notification/outbox dedup violation
            {
                db.ChangeTracker.Clear();
            }
            catch (Exception ex) when (db.IsDeadlock(ex))
            {
                db.ChangeTracker.Clear(); // the deadlock victim retries
            }
        }

        throw AppException.Conflict("The ticket was changed concurrently.", "Please retry.");
    }

    /// <summary>Current assignees, or the complete history (including removed agents). Staff only.</summary>
    public async Task<IReadOnlyList<AssignmentHistoryDto>> ListAsync(
        Guid ticketId,
        bool includeHistory,
        CancellationToken ct
    )
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

        return await (
            from a in db.TicketAssignments.AsNoTracking()
            join m in db.Memberships on a.MembershipId equals m.Id
            join u in db.Users on m.UserId equals u.Id
            where a.TicketId == ticketId && (includeHistory || a.UnassignedAtUtc == null)
            orderby a.AssignedAtUtc, a.Id
            select new AssignmentHistoryDto(
                a.Id,
                m.Id,
                u.Id,
                u.DisplayName,
                a.AssignedAtUtc,
                a.AssignedByMembershipId,
                a.UnassignedAtUtc,
                a.UnassignedByMembershipId,
                a.UnassignedAtUtc == null
            )
        ).ToListAsync(ct);
    }

    private async Task<Ticket> LoadAsync(
        TicketActor actor,
        Guid ticketId,
        int? expectedVersion,
        CancellationToken ct
    )
    {
        if (!actor.IsManager)
        {
            throw AppException.Forbidden("Only Owners and Managers can change assignments.");
        }

        var ticket =
            await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Ticket not found.");
        return expectedVersion is { } v && v != ticket.Version
            ? throw AppException.Conflict(
                "The ticket was changed by someone else.",
                "Reload the ticket and retry."
            )
            : ticket;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Ticket {TicketId}: {Count} agent(s) assigned"
    )]
    private static partial void LogAssigned(ILogger logger, Guid ticketId, int count);
}
