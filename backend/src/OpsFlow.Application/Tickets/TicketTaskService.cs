using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
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

    public async Task<TicketTaskListDto> ListAsync(Guid ticketId, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        RequireStaff(actor);
        var status =
            await TaskTicket(actor, ticketId)
                .AsNoTracking()
                .Select(t => (TicketStatus?)t.Status)
                .SingleOrDefaultAsync(ct) ?? throw TicketNotFound();
        return await BuildListAsync(actor, ticketId, TicketWorkflow.IsFinished(status), ct);
    }

    public async Task<TicketTaskDto> CreateAsync(
        Guid ticketId,
        CreateTicketTaskCommand cmd,
        CancellationToken ct
    )
    {
        var (title, description) = Validate(cmd.Title, cmd.Description);
        var (task, actor) = await WriteAsync(
            ticketId,
            cmd.AssigneeMembershipId is { } requested ? [requested] : null,
            async (actor, ticket, now) =>
            {
                Guid assignee;
                if (actor.IsManager)
                {
                    assignee =
                        cmd.AssigneeMembershipId
                        ?? throw AppException.Validation(
                            "AssigneeMembershipId",
                            "An assignee is required."
                        );
                }
                else if (
                    cmd.AssigneeMembershipId is { } other
                    && other != actor.MembershipId
                )
                {
                    throw AppException.Forbidden(
                        "Agents can only create tasks assigned to themselves."
                    );
                }
                else
                {
                    assignee = actor.MembershipId; // server-derived self-assignment
                }

                await RequireEligibleAssigneeAsync(ticketId, assignee, ct);
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
                return (task, Event(task, TicketTaskChanges.Created));
            },
            ct
        );
        return await MapOneAsync(actor, ticketId, task.Id);
    }

    /// <summary>Replaces title AND description (a null/blank description clears it).</summary>
    public async Task<TicketTaskDto> UpdateDetailsAsync(
        Guid ticketId,
        Guid taskId,
        string title,
        string? description,
        int expectedVersion,
        CancellationToken ct
    )
    {
        var (newTitle, newDescription) = Validate(title, description);
        var (_, actor) = await WriteAsync(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                var task = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                RequireOwnTaskOrManager(actor, task);
                if (task.Title == newTitle && task.Description == newDescription)
                {
                    return (task, null);
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
                return (task, Event(task, TicketTaskChanges.Updated));
            },
            ct
        );
        return await MapOneAsync(actor, ticketId, taskId);
    }

    public async Task<TicketTaskDto> ChangeStatusAsync(
        Guid ticketId,
        Guid taskId,
        TicketTaskStatus status,
        int expectedVersion,
        CancellationToken ct
    )
    {
        if (!Enum.IsDefined(status))
        {
            throw AppException.Validation("Status", "Unknown task status.");
        }

        var (_, actor) = await WriteAsync(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                var task = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                RequireOwnTaskOrManager(actor, task);
                if (task.Status == status)
                {
                    return (task, null);
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
                return (task, Event(task, TicketTaskChanges.StatusChanged));
            },
            ct
        );
        return await MapOneAsync(actor, ticketId, taskId);
    }

    public async Task<TicketTaskDto> ReassignAsync(
        Guid ticketId,
        Guid taskId,
        Guid assigneeMembershipId,
        int expectedVersion,
        CancellationToken ct
    )
    {
        var (_, actor) = await WriteAsync(
            ticketId,
            [assigneeMembershipId],
            async (actor, ticket, now) =>
            {
                RequireManager(actor, "Only Owners and Managers can reassign tasks.");
                var task = await LoadTaskAsync(ticketId, taskId, expectedVersion, ct);
                if (task.AssigneeMembershipId == assigneeMembershipId)
                {
                    return (task, null);
                }

                await RequireEligibleAssigneeAsync(ticketId, assigneeMembershipId, ct);
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
                return (task, Event(task, TicketTaskChanges.Assigned));
            },
            ct
        );
        return await MapOneAsync(actor, ticketId, taskId);
    }

    /// <summary>
    /// Atomic reorder of the COMPLETE task list of the ticket. Unknown ids (other ticket/tenant) are a 400; a missing task (created
    /// concurrently) or a stale version is a 409. Positions become 1..n in the given order.
    /// </summary>
    public async Task<TicketTaskListDto> ReorderAsync(
        Guid ticketId,
        IReadOnlyList<TaskOrderItem> order,
        CancellationToken ct
    )
    {
        if (order.Select(o => o.TaskId).Distinct().Count() != order.Count)
        {
            throw AppException.Validation("Tasks", "Each task may appear only once.");
        }

        var (_, actor) = await WriteAsync<TicketTask?>(
            ticketId,
            null,
            async (actor, ticket, now) =>
            {
                RequireManager(actor, "Only Owners and Managers can reorder tasks.");
                var current = await db
                    .TicketTasks.Where(t => t.TicketId == ticketId)
                    .ToDictionaryAsync(t => t.Id, ct);
                if (order.Any(o => !current.ContainsKey(o.TaskId)))
                {
                    throw AppException.Validation(
                        "Tasks",
                        "Every task must belong to this ticket."
                    );
                }

                if (
                    order.Count != current.Count
                    || order.Any(o => current[o.TaskId].Version != o.Version)
                )
                {
                    throw AppException.Conflict(
                        "The task list was changed by someone else.",
                        "Reload the tasks and retry."
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
                    return (null, null);
                }

                db.TicketActivities.Add(
                    ticket.Record(TicketActivityType.TasksReordered, actor.UserId, now)
                );
                return (
                    null,
                    new TicketTaskEvent(ticketId, null, TicketTaskChanges.Reordered, null)
                );
            },
            ct
        );
        return await BuildListAsync(actor, ticketId, false, CancellationToken.None);
    }

    /// <summary>Kanban indicator for many tickets in ONE grouped query; tickets the caller may not see tasks of are simply absent.</summary>
    public async Task<IReadOnlyList<TicketTaskProgressDto>> GetProgressAsync(
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
        RequireStaff(actor);
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

    private async Task<(T Result, TicketActor Actor)> WriteAsync<T>(
        Guid ticketId,
        IReadOnlyCollection<Guid>? alsoLock,
        Func<TicketActor, Ticket, DateTime, Task<(T Result, TicketTaskEvent? Event)>> change,
        CancellationToken ct
    )
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(alsoLock, ct);
        await using var _ = transaction;
        RequireStaff(actor);
        await access.LockTicketAsync(ticketId, ct);
        // Access is checked AFTER the lock, so a concurrent unassignment cannot slip in between check and commit.
        var ticket =
            await TaskTicket(actor, ticketId).SingleOrDefaultAsync(ct) ?? throw TicketNotFound();
        if (TicketWorkflow.IsFinished(ticket.Status))
        {
            throw AppException.Conflict(
                "Child tasks are read-only while the ticket is resolved or closed.",
                "Reopen the ticket to change its tasks."
            );
        }

        var (result, ev) = await change(actor, ticket, clock.GetUtcNow().UtcDateTime);
        if (ev is not null)
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
            throw Stale();
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
        {
            throw Stale();
        }
        catch (Exception ex) when (db.IsDeadlock(ex))
        {
            throw Stale();
        }

        return (result, actor);
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

    private async Task RequireEligibleAssigneeAsync(
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
            return;
        }

        await access.RequireEligibleAgentsAsync([membershipId], ct); // 400: not an active Agent of this tenant
        throw AppException.Conflict(
            "The agent is not assigned to this ticket.",
            "Assign the agent to the ticket first."
        );
    }

    private async Task<TicketTask> LoadTaskAsync(
        Guid ticketId,
        Guid taskId,
        int expectedVersion,
        CancellationToken ct
    )
    {
        var task =
            await db.TicketTasks.SingleOrDefaultAsync(
                t => t.Id == taskId && t.TicketId == ticketId,
                ct
            ) ?? throw AppException.NotFound("Task not found.");
        return task.Version == expectedVersion ? task : throw Stale();
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

    private static (string Title, string? Description) Validate(string? title, string? description)
    {
        var t = title?.Trim() ?? string.Empty;
        if (t.Length is 0 or > TicketTask.TitleMaxLength)
        {
            throw AppException.Validation(
                "Title",
                $"Title is required and must be at most {TicketTask.TitleMaxLength} characters."
            );
        }

        var d = description?.Trim();
        if (d is { Length: > TicketTask.DescriptionMaxLength })
        {
            throw AppException.Validation(
                "Description",
                $"Description must be at most {TicketTask.DescriptionMaxLength} characters."
            );
        }

        return (t, string.IsNullOrEmpty(d) ? null : d);
    }

    private static TicketTaskEvent Event(TicketTask task, string change) =>
        new(task.TicketId, task.Id, change, task.Version);

    private static void RequireStaff(TicketActor actor)
    {
        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Child tasks are not available to customers.");
        }
    }

    private static void RequireManager(TicketActor actor, string message)
    {
        if (!actor.IsManager)
        {
            throw AppException.Forbidden(message);
        }
    }

    private static void RequireOwnTaskOrManager(TicketActor actor, TicketTask task)
    {
        if (!actor.IsManager && task.AssigneeMembershipId != actor.MembershipId)
        {
            throw AppException.Forbidden("Agents can only change their own tasks.");
        }
    }

    private static AppException TicketNotFound() => AppException.NotFound("Ticket not found.");

    private static AppException Stale() =>
        AppException.Conflict(
            "The task was changed by someone else.",
            "Reload the tasks and retry."
        );
}
