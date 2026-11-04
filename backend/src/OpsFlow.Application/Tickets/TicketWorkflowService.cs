using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Notifications;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// The ticket state machine in one place: allowed transitions (<see cref="TicketWorkflow"/>), who may perform them, SLA cycle
/// finalization on resolve and a NEW SLA cycle on reopen. Every change persists status, SLA cycle and timeline together and is
/// guarded by the ticket Version concurrency token, so simultaneous transitions/reopens never overwrite each other.
/// </summary>
public sealed partial class TicketWorkflowService(
    IOpsFlowDbContext db,
    TicketAccess access,
    TicketService tickets,
    NotificationService notifications,
    TimeProvider clock,
    ILogger<TicketWorkflowService> logger
)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Moves along InProgress / OnHold / Resolved / Closed. Owners and Managers can do every allowed transition; an Agent only on tickets they are assigned to (not Closed).</summary>
    public async Task<ActionResult<TicketDetailDto>> TransitionAsync(
        Guid ticketId,
        TicketStatus target,
        int? expectedVersion,
        CancellationToken ct
    )
    {
        // Locks the actor's rows and re-reads the current role until commit.
        if (await access.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction; // every early return below rolls back: nothing is committed
        var loaded = await LoadAsync(actor, ticketId, expectedVersion, ct);
        if (loaded.Result is ObjectResult loadFailure)
        {
            return loadFailure;
        }

        var ticket = loaded.Value!;
        if (target is TicketStatus.Open or TicketStatus.Assigned)
        {
            return new BadRequestObjectResult(
                new ProblemDetails
                {
                    Title = "Open and Assigned are set by assignments; use the assignment endpoints.",
                }
            );
        }

        if (!TicketWorkflow.IsAllowed(ticket.Status, target))
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = $"A ticket cannot move from {ticket.Status} to {target}." }
            );
        }

        if (actor.IsCustomer)
        {
            return ActorAccess.Forbidden(
                "Customers cannot change the ticket status; they can reopen a resolved ticket."
            );
        }

        if (
            !actor.IsManager
            && (
                target == TicketStatus.Closed
                || !await access.IsActiveAssigneeAsync(ticketId, actor.MembershipId, ct)
            )
        )
        {
            return ActorAccess.Forbidden(
                "Agents can only update tickets assigned to them, and cannot close them."
            );
        }

        if (
            target is TicketStatus.InProgress or TicketStatus.OnHold or TicketStatus.Resolved
            && !await access.EffectiveAssignments().AnyAsync(a => a.TicketId == ticketId, ct)
        )
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "The ticket has no eligible assignee.",
                    Detail = "Assign an active agent first.",
                }
            );
        }

        var now = Now;
        var old = ticket.Status;
        if (target == TicketStatus.Resolved)
        {
            var cycle = await db.TicketSlaCycles.SingleOrDefaultAsync(
                c => c.TicketId == ticketId && c.ResolvedAtUtc == null,
                ct
            );
            if (cycle is null)
            {
                return new ConflictObjectResult(
                    new ProblemDetails { Title = "The ticket has no active SLA cycle." }
                );
            }

            cycle.Resolve(now);
            ticket.MarkResolved(now);
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.Resolved,
                    actor.UserId,
                    now,
                    old.ToString(),
                    target.ToString()
                )
            );
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.SlaCycleCompleted,
                    null,
                    now,
                    newValue: SlaEvaluation.Resolution(cycle, now).ToString(),
                    relatedId: cycle.Id
                )
            );
        }
        else
        {
            ticket.SetStatus(target, now);
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.StatusChanged,
                    actor.UserId,
                    now,
                    old.ToString(),
                    target.ToString()
                )
            );
        }

        // Status/resolve notification in the same commit (staff + customer, excluding the actor).
        await notifications.StageAsync(
            ticket,
            target == TicketStatus.Resolved
                ? NotificationType.TicketResolved
                : NotificationType.TicketStatusChanged,
            actor.UserId,
            $"status:{ticketId}:{ticket.Version}",
            toStaff: true,
            toCustomer: true,
            detail: target.ToString(),
            ct: ct
        );
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        LogChanged(logger, ticketId, old, target);
        return await tickets.GetAsync(ticketId, ct);
    }

    /// <summary>
    /// Reopens a Resolved/Closed ticket and starts a NEW SLA cycle (resolution target only, measured from now). Earlier cycles stay
    /// exactly as they were. Staff and assigned Agents may reopen; a Customer may reopen their own RESOLVED ticket.
    /// </summary>
    public async Task<ActionResult<TicketDetailDto>> ReopenAsync(
        Guid ticketId,
        int? expectedVersion,
        CancellationToken ct
    )
    {
        if (await access.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction; // every early return below rolls back: nothing is committed
        var loaded = await LoadAsync(actor, ticketId, expectedVersion, ct);
        if (loaded.Result is ObjectResult loadFailure)
        {
            return loadFailure;
        }

        var ticket = loaded.Value!;
        if (!TicketWorkflow.IsFinished(ticket.Status))
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "Only a resolved or closed ticket can be reopened." }
            );
        }

        if (actor.IsCustomer && ticket.Status != TicketStatus.Resolved)
        {
            return ActorAccess.Forbidden("Customers can only reopen a resolved ticket.");
        }

        if (
            actor.Role == TenantRole.Agent
            && !await access.IsActiveAssigneeAsync(ticketId, actor.MembershipId, ct)
        )
        {
            return ActorAccess.Forbidden("Agents can only reopen tickets assigned to them.");
        }

        var policy = await db
            .SlaPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Priority == ticket.Priority && p.IsActive, ct);
        if (policy is null)
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "No active SLA policy exists for this priority." }
            );
        }

        var hasAssignees = await access
            .EffectiveAssignments()
            .AnyAsync(a => a.TicketId == ticketId, ct); // only ELIGIBLE agents count

        var now = Now;
        var old = ticket.Status;
        var next = hasAssignees ? TicketStatus.Assigned : TicketStatus.Open;
        ticket.Reopen(next, now);
        var cycle = TicketSlaCycle.Start(
            ticket,
            ticket.NextCycleNumber(),
            SlaCycleReason.Reopened,
            policy,
            now
        );
        db.TicketSlaCycles.Add(cycle);
        db.TicketActivities.Add(
            ticket.Record(
                TicketActivityType.Reopened,
                actor.UserId,
                now,
                old.ToString(),
                next.ToString()
            )
        );
        db.TicketActivities.Add(
            ticket.Record(
                TicketActivityType.SlaCycleStarted,
                null,
                now,
                newValue: cycle.CycleNumber.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                relatedId: cycle.Id
            )
        );

        await notifications.StageAsync(
            ticket,
            NotificationType.TicketReopened,
            actor.UserId,
            $"reopen:{ticketId}:{ticket.Version}",
            toStaff: true,
            toCustomer: true,
            detail: next.ToString(),
            ct: ct
        );
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        LogChanged(logger, ticketId, old, next);
        return await tickets.GetAsync(ticketId, ct);
    }

    private async Task<ActionResult<Ticket>> LoadAsync(
        TicketActor actor,
        Guid ticketId,
        int? expectedVersion,
        CancellationToken ct
    )
    {
        var ticket = await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct);
        if (ticket is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Ticket not found." });
        }

        return expectedVersion is { } v && v != ticket.Version ? ChangedByOthers() : ticket;
    }

    /// <returns>Null when saved; the 409 when a concurrent writer won.</returns>
    private async Task<ObjectResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedByOthers();
        }
        catch (Exception ex) when (db.IsDeadlock(ex)) // a deadlock victim is a conflict too
        {
            return ChangedByOthers();
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActiveSlaCycle)
                || db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence)
                || db.IsUniqueViolation(ex, UniqueConstraintKind.NotificationDedup)
                || db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup)
            ) // an activity-sequence clash between concurrent writers is a conflict, not a 500
        {
            return ChangedByOthers();
        }
    }

    private static ConflictObjectResult ChangedByOthers() =>
        new(
            new ProblemDetails
            {
                Title = "The ticket was changed by someone else.",
                Detail = "Reload the ticket and retry.",
            }
        );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Ticket {TicketId} moved {From} -> {To}"
    )]
    private static partial void LogChanged(
        ILogger logger,
        Guid ticketId,
        TicketStatus from,
        TicketStatus to
    );
}
