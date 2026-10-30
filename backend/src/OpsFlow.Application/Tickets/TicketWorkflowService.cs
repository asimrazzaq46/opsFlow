using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// The ticket state machine in one place: allowed transitions (<see cref="TicketWorkflow"/>), who may perform them, SLA cycle
/// finalization on resolve and a NEW SLA cycle on reopen. Every change persists status, SLA cycle and timeline together and is
/// guarded by the ticket Version concurrency token, so simultaneous transitions/reopens never overwrite each other.
/// </summary>
public sealed partial class TicketWorkflowService(IOpsFlowDbContext db, TicketAccess access, TicketService tickets, TimeProvider clock, ILogger<TicketWorkflowService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Moves along InProgress / OnHold / Resolved / Closed. Owners and Managers can do every allowed transition; an Agent only on tickets they are assigned to (not Closed).</summary>
    public async Task<TicketDetailDto> TransitionAsync(Guid ticketId, TicketStatus target, int? expectedVersion, CancellationToken ct)
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct); // UPDATED: actor ki rows lock + current role, commit tak
        await using var _ = transaction;
        var ticket = await LoadAsync(actor, ticketId, expectedVersion, ct);
        if (target is TicketStatus.Open or TicketStatus.Assigned)
        {
            throw AppException.BadRequest("Open and Assigned are set by assignments; use the assignment endpoints.");
        }

        if (!TicketWorkflow.IsAllowed(ticket.Status, target))
        {
            throw AppException.Conflict($"A ticket cannot move from {ticket.Status} to {target}.");
        }

        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Customers cannot change the ticket status; they can reopen a resolved ticket.");
        }

        if (!actor.IsManager && (target == TicketStatus.Closed || !await access.IsActiveAssigneeAsync(ticketId, actor.MembershipId, ct)))
        {
            throw AppException.Forbidden("Agents can only update tickets assigned to them, and cannot close them.");
        }

        if (target is TicketStatus.InProgress or TicketStatus.OnHold or TicketStatus.Resolved
            && !await access.EffectiveAssignments().AnyAsync(a => a.TicketId == ticketId, ct))
        {
            throw AppException.Conflict("The ticket has no eligible assignee.", "Assign an active agent first.");
        }

        var now = Now;
        var old = ticket.Status;
        if (target == TicketStatus.Resolved)
        {
            var cycle = await db.TicketSlaCycles.SingleOrDefaultAsync(c => c.TicketId == ticketId && c.ResolvedAtUtc == null, ct)
                ?? throw AppException.Conflict("The ticket has no active SLA cycle.");
            cycle.Resolve(now);
            ticket.MarkResolved(now);
            db.TicketActivities.Add(ticket.Record(TicketActivityType.Resolved, actor.UserId, now, old.ToString(), target.ToString()));
            db.TicketActivities.Add(ticket.Record(TicketActivityType.SlaCycleCompleted, null, now, newValue: SlaEvaluation.Resolution(cycle, now).ToString(), relatedId: cycle.Id));
        }
        else
        {
            ticket.SetStatus(target, now);
            db.TicketActivities.Add(ticket.Record(TicketActivityType.StatusChanged, actor.UserId, now, old.ToString(), target.ToString()));
        }

        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        LogChanged(logger, ticketId, old, target);
        return await tickets.GetAsync(ticketId, ct);
    }

    /// <summary>
    /// Reopens a Resolved/Closed ticket and starts a NEW SLA cycle (resolution target only, measured from now). Earlier cycles stay
    /// exactly as they were. Staff and assigned Agents may reopen; a Customer may reopen their own RESOLVED ticket.
    /// </summary>
    public async Task<TicketDetailDto> ReopenAsync(Guid ticketId, int? expectedVersion, CancellationToken ct)
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct);
        await using var _ = transaction;
        var ticket = await LoadAsync(actor, ticketId, expectedVersion, ct);
        if (!TicketWorkflow.IsFinished(ticket.Status))
        {
            throw AppException.Conflict("Only a resolved or closed ticket can be reopened.");
        }

        if (actor.IsCustomer && ticket.Status != TicketStatus.Resolved)
        {
            throw AppException.Forbidden("Customers can only reopen a resolved ticket.");
        }

        if (actor.Role == TenantRole.Agent && !await access.IsActiveAssigneeAsync(ticketId, actor.MembershipId, ct))
        {
            throw AppException.Forbidden("Agents can only reopen tickets assigned to them.");
        }

        var policy = await db.SlaPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Priority == ticket.Priority && p.IsActive, ct)
            ?? throw AppException.Conflict("No active SLA policy exists for this priority.");
        var hasAssignees = await access.EffectiveAssignments().AnyAsync(a => a.TicketId == ticketId, ct); // UPDATED: sirf ELIGIBLE agents ginay jate hain

        var now = Now;
        var old = ticket.Status;
        var next = hasAssignees ? TicketStatus.Assigned : TicketStatus.Open;
        ticket.Reopen(next, now);
        var cycle = TicketSlaCycle.Start(ticket, ticket.NextCycleNumber(), SlaCycleReason.Reopened, policy, now);
        db.TicketSlaCycles.Add(cycle);
        db.TicketActivities.Add(ticket.Record(TicketActivityType.Reopened, actor.UserId, now, old.ToString(), next.ToString()));
        db.TicketActivities.Add(ticket.Record(TicketActivityType.SlaCycleStarted, null, now, newValue: cycle.CycleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), relatedId: cycle.Id));

        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        LogChanged(logger, ticketId, old, next);
        return await tickets.GetAsync(ticketId, ct);
    }

    private async Task<Ticket> LoadAsync(TicketActor actor, Guid ticketId, int? expectedVersion, CancellationToken ct)
    {
        var ticket = await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Ticket not found.");
        return expectedVersion is { } v && v != ticket.Version
            ? throw AppException.Conflict("The ticket was changed by someone else.", "Reload the ticket and retry.")
            : ticket;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AppException.Conflict("The ticket was changed by someone else.", "Reload the ticket and retry.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActiveSlaCycle) || db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence)) // UPDATED: concurrent writers ka activity-sequence clash bhi conflict hai, 500 nahi
        {
            throw AppException.Conflict("The ticket was changed by someone else.", "Reload the ticket and retry.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ticket {TicketId} moved {From} -> {To}")]
    private static partial void LogChanged(ILogger logger, Guid ticketId, TicketStatus from, TicketStatus to);
}
