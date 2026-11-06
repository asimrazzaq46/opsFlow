using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Internal child tasks of a ticket. Access: Owners/Managers on every ticket of the tenant; an Agent only while they are an EFFECTIVE
/// assignee of the parent ticket (and may change only their own tasks); Customers never. Every write runs in an authorized transaction
/// that locks the actor (and target assignee) membership rows and the parent ticket row, so creation, reordering and agent unassignment
/// are serialized per ticket. Task changes, their audit entries and the realtime outbox event commit together. Tasks never change the
/// parent ticket's status, SLA or assignments.
/// </summary>
public sealed class TicketTaskService(
    IOpsFlowDbContext db,
    TicketAccess access,
    OutboxWriter outbox,
    TimeProvider clock
)
{
    /// <summary>Largest Kanban board: 6 status columns x the GraphQL page maximum of 50 cards.</summary>
    public const int MaxProgressTickets = 300;

    public async Task<ActionResult<TicketTaskListDto>> ListAsync(Guid ticketId, CancellationToken ct)
    {
        if (await access.TryResolveAsync(ct) is not { } actor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (actor.IsCustomer)
        {
            return CustomersExcluded();
        }

        var status = await TaskTicket(actor, ticketId)
            .AsNoTracking()
            .Select(t => (TicketStatus?)t.Status)
            .SingleOrDefaultAsync(ct);
        return status is null
            ? await NoTaskAccessAsync(actor, ticketId, ct)
            : await BuildListAsync(actor, ticketId, TicketWorkflow.IsFinished(status.Value), ct);
    }

    public async Task<ActionResult<TicketTaskDto>> CreateAsync(
        Guid ticketId,
        CreateTicketTaskCommand cmd,
        CancellationToken ct
    )
    {
        if (Validate(cmd.Title, cmd.Description, out var title, out var description) is { } invalid)
        {
            return invalid;
        }

        var taskId = Guid.Empty;
        var written = await WriteAsync(
            ticketId,
            cmd.AssigneeMembershipId is { } requested ? [requested] : null,
            async (actor, ticket, now) =>
            {
                Guid assignee;
                if (actor.IsManager)
                {
                    if (cmd.AssigneeMembershipId is not { } chosen)
                    {
                        return FieldError("AssigneeMembershipId", "An assignee is required.");
                    }

                    assignee = chosen;
                }
                else if (
                    cmd.AssigneeMembershipId is { } other
                    && other != actor.MembershipId
                )
                {
                    return ActorAccess.Forbidden(
                        "Agents can only create tasks assigned to themselves."
                    );
                }
                else
                {
                    assignee = actor.MembershipId; // server-derived self-assignment
                }

                if (await CheckEligibleAssigneeAsync(ticketId, assignee, ct) is { } ineligible)
                {
                    return ineligible;
                }

                var sortOrder =
                    (
                        await db
                            .TicketTasks.Where(t => t.TicketId == ticketId)
                            .MaxAsync(t => (int?)t.SortOrder, ct) ?? 0
                    ) + 1;
                var task = TicketTask.Create(
                    ticket.TenantId,
                    ticketId,
                    title,
                    description,
                    assignee,
                    sortOrder,
                    actor.MembershipId,
                    now
                );
                db.TicketTasks.Add(task);
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.TaskCreated,
                        actor.UserId,
                        now,
                        newValue: assignee.ToString(),
                        relatedId: task.Id
                    )
                );
                taskId = task.Id;
                return Event(task, TicketTaskChanges.Created);
            },
            ct
        );
        return written.Result is ObjectResult failure
            ? failure
            : await MapOneAsync(written.Value!, ticketId, taskId);
    }

    /// <summary>Replaces title AND description (a null/blank description clears it).</summary>
    public async Task<ActionResult<TicketTaskDto>> UpdateDetailsAsync(
        Guid ticketId,
        Guid taskId,
        string title,
        string? description,
        int expectedVersion,
        CancellationToken ct
    )
    {
        if (Validate(title, description, out var newTitle, out var newDescription) is { } invalid)
        {
            return invalid;
        }

        var written = await WriteAsync(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                var loaded = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                if (loaded.Result is ObjectResult missing)
                {
                    return missing;
                }

                var task = loaded.Value!;
                if (NotOwnTaskOrManager(actor, task) is { } forbidden)
                {
                    return forbidden;
                }

                if (task.Title == newTitle && task.Description == newDescription)
                {
                    return NoChange;
                }

                task.UpdateDetails(newTitle, newDescription, actor.MembershipId, now);
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.TaskDetailsUpdated,
                        actor.UserId,
                        now,
                        relatedId: task.Id
                    )
                );
                return Event(task, TicketTaskChanges.Updated);
            },
            ct
        );
        return written.Result is ObjectResult failure
            ? failure
            : await MapOneAsync(written.Value!, ticketId, taskId);
    }

    public async Task<ActionResult<TicketTaskDto>> ChangeStatusAsync(
        Guid ticketId,
        Guid taskId,
        TicketTaskStatus status,
        int expectedVersion,
        CancellationToken ct
    )
    {
        if (!Enum.IsDefined(status))
        {
            return FieldError("Status", "Unknown task status.");
        }

        var written = await WriteAsync(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                var loaded = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                if (loaded.Result is ObjectResult missing)
                {
                    return missing;
                }

                var task = loaded.Value!;
                if (NotOwnTaskOrManager(actor, task) is { } forbidden)
                {
                    return forbidden;
                }

                if (task.Status == status)
                {
                    return NoChange;
                }

                var old = task.Status;
                task.ChangeStatus(status, actor.MembershipId, now);
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.TaskStatusChanged,
                        actor.UserId,
                        now,
                        old.ToString(),
                        status.ToString(),
                        task.Id
                    )
                );
                return Event(task, TicketTaskChanges.StatusChanged);
            },
            ct
        );
        return written.Result is ObjectResult failure
            ? failure
            : await MapOneAsync(written.Value!, ticketId, taskId);
    }

    public async Task<ActionResult<TicketTaskDto>> ReassignAsync(
        Guid ticketId,
        Guid taskId,
        Guid assigneeMembershipId,
        int expectedVersion,
        CancellationToken ct
    )
    {
        var written = await WriteAsync(
            ticketId,
            [assigneeMembershipId],
            async (actor, ticket, now) =>
            {
                if (!actor.IsManager)
                {
                    return ActorAccess.Forbidden("Only Owners and Managers can reassign tasks.");
                }

                var loaded = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                if (loaded.Result is ObjectResult missing)
                {
                    return missing;
                }

                var task = loaded.Value!;
                if (task.AssigneeMembershipId == assigneeMembershipId)
                {
                    return NoChange;
                }

                if (await CheckEligibleAssigneeAsync(ticketId, assigneeMembershipId, ct) is { } ineligible)
                {
                    return ineligible;
                }

                var old = task.AssigneeMembershipId;
                task.Reassign(assigneeMembershipId, actor.MembershipId, now);
                db.TicketActivities.Add(
                    ticket.Record(
                        TicketActivityType.TaskAssigned,
                        actor.UserId,
                        now,
                        old.ToString(),
                        assigneeMembershipId.ToString(),
                        task.Id
                    )
                );
                return Event(task, TicketTaskChanges.Assigned);
            },
            ct
        );
        return written.Result is ObjectResult failure
            ? failure
            : await MapOneAsync(written.Value!, ticketId, taskId);
    }

    /// <summary>
    /// Atomic reorder of the COMPLETE task list of the ticket. Unknown ids (other ticket/tenant) are a 400; a missing task (created
    /// concurrently) or a stale version is a 409. Positions become 1..n in the given order.
    /// </summary>
    public async Task<ActionResult<TicketTaskListDto>> ReorderAsync(
        Guid ticketId,
        IReadOnlyList<TaskOrderItem> order,
        CancellationToken ct
    )
    {
        if (order.Select(o => o.TaskId).Distinct().Count() != order.Count)
        {
            return FieldError("Tasks", "Each task may appear only once.");
        }

        var written = await WriteAsync(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                if (!actor.IsManager)
                {
                    return ActorAccess.Forbidden("Only Owners and Managers can reorder tasks.");
                }

                var current = await db
                    .TicketTasks.Where(t => t.TicketId == ticketId)
                    .ToDictionaryAsync(t => t.Id, ct);
                if (order.Any(o => !current.ContainsKey(o.TaskId)))
                {
                    return FieldError("Tasks", "Every task must belong to this ticket.");
                }

                if (
                    order.Count != current.Count
                    || order.Any(o => current[o.TaskId].Version != o.Version)
                )
                {
                    return new ConflictObjectResult(
                        new ProblemDetails
                        {
                            Title = "The task list was changed by someone else.",
                            Detail = "Reload the tasks and retry.",
                        }
                    );
                }

                var moved = false;
                for (var i = 0; i < order.Count; i++)
                {
                    var task = current[order[i].TaskId];
                    if (task.SortOrder != i + 1)
                    {
                        task.MoveTo(i + 1, actor.MembershipId, now);
                        moved = true;
                    }
                }

                if (!moved)
                {
                    return NoChange;
                }

                db.TicketActivities.Add(
                    ticket.Record(TicketActivityType.TasksReordered, actor.UserId, now)
                );
                return new TicketTaskEvent(ticketId, null, TicketTaskChanges.Reordered, null);
            },
            ct
        );
        return written.Result is ObjectResult failure
            ? failure
            : await BuildListAsync(written.Value!, ticketId, false, CancellationToken.None);
    }

    /// <summary>Kanban indicator for many tickets in ONE grouped query; tickets the caller may not see tasks of are simply absent.</summary>
    public async Task<IReadOnlyList<TicketTaskProgressDto>> GetProgressAsync(
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Child tasks are not available to customers.");
        }

        var ids = ticketIds.Distinct().ToList();
        if (ids.Count > MaxProgressTickets)
        {
            throw AppException.Validation(
                "TicketIds",
                $"At most {MaxProgressTickets} tickets per request."
            );
        }

        var query = db.TicketTasks.AsNoTracking().Where(t => ids.Contains(t.TicketId));
        if (!actor.IsManager)
        {
            var effective = access.EffectiveAssignments();
            query = query.Where(t =>
                effective.Any(a => a.TicketId == t.TicketId && a.MembershipId == actor.MembershipId)
            );
        }

        return await query
            .GroupBy(t => t.TicketId)
            .Select(g => new TicketTaskProgressDto(
                g.Key,
                g.Count(),
                g.Count(t => t.Status == TicketTaskStatus.Done)
            ))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Runs one task write in an authorized transaction. The change returns a failure (nothing is committed: the transaction is disposed
    /// without commit), <see cref="NoChange"/>, or the realtime event to stage. The result carries the actor on success.
    /// </summary>
    private async Task<ActionResult<TicketActor>> WriteAsync(
        Guid ticketId,
        IReadOnlyCollection<Guid>? alsoLock,
        Func<TicketActor, Ticket, DateTime, Task<ActionResult<TicketTaskEvent?>>> change,
        CancellationToken ct
    )
    {
        if (await access.TryBeginAuthorizedAsync(alsoLock, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction;
        if (actor.IsCustomer)
        {
            return CustomersExcluded();
        }

        if (!await access.TryLockTicketAsync(ticketId, ct))
        {
            return TicketNotFound();
        }

        // Access is checked AFTER the lock, so a concurrent unassignment cannot slip in between check and commit.
        var ticket = await TaskTicket(actor, ticketId).SingleOrDefaultAsync(ct);
        if (ticket is null)
        {
            return await NoTaskAccessAsync(actor, ticketId, ct);
        }

        if (TicketWorkflow.IsFinished(ticket.Status))
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "Child tasks are read-only while the ticket is resolved or closed.",
                    Detail = "Reopen the ticket to change its tasks.",
                }
            );
        }

        var outcome = await change(actor, ticket, clock.GetUtcNow().UtcDateTime);
        if (outcome.Result is ObjectResult failure)
        {
            return failure;
        }

        if (outcome.Value is { } ev)
        {
            outbox.Stage(
                ticket.TenantId,
                OutboxEvents.TicketTaskChanged,
                ticketId,
                ev,
                $"task:{ev.TaskId ?? ticketId}:{Guid.NewGuid():N}",
                OutboxChannels.Realtime
            );
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
        {
            return Stale();
        }
        catch (Exception ex) when (db.IsDeadlock(ex))
        {
            return Stale();
        }

        return actor;
    }

    /// <summary>
    /// The ticket, if the caller may work with its tasks: Owners/Managers on any ticket of the tenant, an Agent only while an EFFECTIVE
    /// assignee. Anything else yields no row, i.e. 404 without revealing whether the ticket exists.
    /// </summary>
    private IQueryable<Ticket> TaskTicket(TicketActor actor, Guid ticketId)
    {
        var tickets = db.Tickets.Where(t => t.Id == ticketId);
        if (actor.IsManager)
        {
            return tickets;
        }

        var effective = access.EffectiveAssignments();
        return tickets.Where(t =>
            effective.Any(a => a.TicketId == t.Id && a.MembershipId == actor.MembershipId)
        );
    }

    /// <returns>Null when the agent is assigned to the ticket; otherwise the 400 (not an eligible Agent) or 409 (not assigned).</returns>
    private async Task<ObjectResult?> CheckEligibleAssigneeAsync(
        Guid ticketId,
        Guid membershipId,
        CancellationToken ct
    )
    {
        if (
            await access
                .EffectiveAssignments()
                .AnyAsync(a => a.TicketId == ticketId && a.MembershipId == membershipId, ct)
        )
        {
            return null;
        }

        if (await access.GetEligibleAgentsAsync([membershipId], ct) is null)
        {
            return new BadRequestObjectResult(
                new ProblemDetails
                {
                    Title = "Every assignee must be an active Agent of this organization.",
                }
            );
        }

        return new ConflictObjectResult(
            new ProblemDetails
            {
                Title = "The agent is not assigned to this ticket.",
                Detail = "Assign the agent to the ticket first.",
            }
        );
    }

    private async Task<ActionResult<TicketTask>> LoadTaskAsync(
        Guid ticketId,
        Guid taskId,
        int expectedVersion,
        CancellationToken ct
    )
    {
        var task = await db.TicketTasks.SingleOrDefaultAsync(
            t => t.Id == taskId && t.TicketId == ticketId,
            ct
        );
        if (task is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Task not found." });
        }

        return task.Version == expectedVersion ? task : Stale();
    }

    private async Task<TicketTaskListDto> BuildListAsync(
        TicketActor actor,
        Guid ticketId,
        bool readOnly,
        CancellationToken ct
    )
    {
        var tasks = await QueryAsync(actor, ticketId, null, readOnly, ct);
        return new TicketTaskListDto(
            tasks,
            new TicketTaskSummaryDto(
                tasks.Count,
                tasks.Count(t => t.Status == TicketTaskStatus.Todo),
                tasks.Count(t => t.Status == TicketTaskStatus.InProgress),
                tasks.Count(t => t.Status == TicketTaskStatus.Done)
            ),
            readOnly,
            !readOnly,
            actor.IsManager && !readOnly
        );
    }

    private async Task<TicketTaskDto> MapOneAsync(TicketActor actor, Guid ticketId, Guid taskId) =>
        (await QueryAsync(actor, ticketId, taskId, false, CancellationToken.None))[0]; // committed already: never cancel the read-back

    private async Task<List<TicketTaskDto>> QueryAsync(
        TicketActor actor,
        Guid ticketId,
        Guid? taskId,
        bool readOnly,
        CancellationToken ct
    )
    {
        var rows = await (
            from t in db.TicketTasks.AsNoTracking()
            join m in db.Memberships on t.AssigneeMembershipId equals m.Id
            join u in db.Users on m.UserId equals u.Id
            where t.TicketId == ticketId && (taskId == null || t.Id == taskId)
            orderby t.SortOrder, t.CreatedAtUtc, t.Id
            select new
            {
                Task = t,
                u.DisplayName,
                Eligible = m.Role == TenantRole.Agent
                    && m.Status == MembershipStatus.Active
                    && u.Status == UserStatus.Active
                    && db.TicketAssignments.Any(a =>
                        a.TicketId == t.TicketId
                        && a.MembershipId == m.Id
                        && a.UnassignedAtUtc == null
                    ),
            }
        ).ToListAsync(ct);
        return rows.ConvertAll(r =>
        {
            var t = r.Task;
            var mayChange =
                !readOnly && (actor.IsManager || t.AssigneeMembershipId == actor.MembershipId);
            return new TicketTaskDto(
                t.Id,
                t.TicketId,
                t.Title,
                t.Description,
                t.Status,
                t.SortOrder,
                t.AssigneeMembershipId,
                r.DisplayName,
                r.Eligible,
                t.CreatedAtUtc,
                t.UpdatedAtUtc,
                t.CompletedAtUtc,
                t.Version,
                mayChange,
                mayChange
            );
        });
    }

    /// <returns>Null when valid; otherwise the 400 for the first invalid field.</returns>
    private static BadRequestObjectResult? Validate(
        string? title,
        string? description,
        out string trimmedTitle,
        out string? trimmedDescription
    )
    {
        trimmedTitle = title?.Trim() ?? string.Empty;
        trimmedDescription = description?.Trim();
        if (trimmedDescription is { Length: 0 })
        {
            trimmedDescription = null;
        }

        if (trimmedTitle.Length is 0 or > TicketTask.TitleMaxLength)
        {
            return FieldError(
                "Title",
                $"Title is required and must be at most {TicketTask.TitleMaxLength} characters."
            );
        }

        return trimmedDescription is { Length: > TicketTask.DescriptionMaxLength }
            ? FieldError(
                "Description",
                $"Description must be at most {TicketTask.DescriptionMaxLength} characters."
            )
            : null;
    }

    private static TicketTaskEvent Event(TicketTask task, string change) =>
        new(task.TicketId, task.Id, change, task.Version);

    /// <summary>"No change to persist": a null event leaves the outbox untouched while the write still commits.</summary>
    private static readonly ActionResult<TicketTaskEvent?> NoChange = new(default(TicketTaskEvent));

    private static ObjectResult CustomersExcluded() =>
        ActorAccess.Forbidden("Child tasks are not available to customers.");

    private static ObjectResult? NotOwnTaskOrManager(TicketActor actor, TicketTask task) =>
        !actor.IsManager && task.AssigneeMembershipId != actor.MembershipId
            ? ActorAccess.Forbidden("Agents can only change their own tasks.")
            : null;

    private static BadRequestObjectResult FieldError(string field, string message) =>
        new(new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = [message] }));

    /// <summary>A ticket the Agent can see in the workspace but is not assigned to is forbidden (not "missing"); other tickets stay hidden.</summary>
    private async Task<ObjectResult> NoTaskAccessAsync(TicketActor actor, Guid ticketId, CancellationToken ct) =>
        await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct)
            ? ActorAccess.Forbidden(TicketAccess.AgentMustBeAssignedToOpen)
            : TicketNotFound();

    private static NotFoundObjectResult TicketNotFound() =>
        new(new ProblemDetails { Title = "Ticket not found." });

    private static ConflictObjectResult Stale() =>
        new(
            new ProblemDetails
            {
                Title = "The task was changed by someone else.",
                Detail = "Reload the tasks and retry.",
            }
        );
}
