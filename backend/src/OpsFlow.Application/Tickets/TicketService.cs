using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>Ticket creation, reading (list/detail/activity/SLA) and field updates. Status changes and assignments live in their own services.</summary>
public sealed partial class TicketService(
    IOpsFlowDbContext db,
    TicketAccess access,
    TicketAudience audience,
    ITenantContext tenantContext,
    NotificationService notifications,
    OutboxWriter outbox,
    TimeProvider clock,
    ILogger<TicketService> logger
)
{
    private const int MaxPageSize = 100;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- create

    /// <summary>
    /// Requester rules: a Customer creates tickets only for the contact linked to their account; an Agent only for themselves
    /// (and is self-assigned); Owners/Managers may pick an active customer and assign agents. Number, ticket, first SLA cycle,
    /// assignments and timeline are ONE transaction.
    /// </summary>
    public async Task<ActionResult<TicketDetailDto>> CreateAsync(CreateTicketCommand cmd, CancellationToken ct)
    {
        // Starts the transaction with the actor's and any assignees' rows locked, so authorization/eligibility cannot change before commit.
        if (await access.TryBeginAuthorizedAsync(cmd.AssigneeMembershipIds, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction;
        var category = await db
            .TicketCategories.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == cmd.CategoryId && c.IsActive, ct);
        if (category is null)
        {
            return CategoryUnavailable();
        }

        var priority = cmd.Priority ?? TicketPriority.Medium;
        Guid? customerId = null;
        List<Guid> assignees = [];
        switch (actor.Role)
        {
            case TenantRole.Customer:
                if (
                    cmd.CustomerId is not null
                    || cmd.Priority is not null
                    || cmd.AssigneeMembershipIds is { Count: > 0 }
                )
                {
                    return ActorAccess.Forbidden(
                        "Customers cannot choose the customer, priority or assignees."
                    );
                }

                customerId = await db
                    .Customers.AsNoTracking()
                    .Where(c => c.UserId == actor.UserId && c.IsActive)
                    .Select(c => (Guid?)c.Id)
                    .SingleOrDefaultAsync(ct);
                if (customerId is null)
                {
                    return ActorAccess.Forbidden(
                        "Your account is not linked to an active customer contact."
                    );
                }
                break;
            case TenantRole.Agent:
                if (cmd.CustomerId is not null || cmd.AssigneeMembershipIds is { Count: > 0 })
                {
                    return ActorAccess.Forbidden("Agents can only create tickets for themselves.");
                }

                assignees = [actor.MembershipId]; // the agent-as-requester rule: self-assigned
                break;
            default:
                if (cmd.CustomerId is { } requested)
                {
                    customerId = await db
                        .Customers.AsNoTracking()
                        .Where(c => c.Id == requested && c.IsActive)
                        .Select(c => (Guid?)c.Id)
                        .SingleOrDefaultAsync(ct);
                    if (customerId is null)
                    {
                        return new BadRequestObjectResult(
                            new ProblemDetails { Title = "The customer is not available." }
                        );
                    }
                }

                if (cmd.AssigneeMembershipIds is { Count: > 0 } ids)
                {
                    if (await access.GetEligibleAgentsAsync(ids, ct) is not { } eligible)
                    {
                        return new BadRequestObjectResult(
                            new ProblemDetails
                            {
                                Title = "Every assignee must be an active Agent of this organization.",
                            }
                        );
                    }

                    assignees = eligible;
                }

                break;
        }

        var policy = await db
            .SlaPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Priority == priority && p.IsActive, ct);
        if (policy is null)
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "No active SLA policy exists for this priority.",
                    Detail = "An Owner must initialize the tenant defaults.",
                }
            );
        }

        var now = Now;
        if (await NextNumberAsync(ct) is not { } number)
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "Ticket numbering is not initialized for this organization.",
                    Detail = "An Owner must initialize the tenant defaults.",
                }
            );
        }
        var ticket = Ticket.Create(
            tenantContext.TenantId!.Value,
            number,
            cmd.Title,
            cmd.Description,
            priority,
            category.Id,
            customerId,
            actor.UserId,
            now
        );
        db.Tickets.Add(ticket);
        db.TicketActivities.Add(
            ticket.Record(
                TicketActivityType.Created,
                actor.UserId,
                now,
                newValue: ticket.Status.ToString()
            )
        );

        var cycle = TicketSlaCycle.Start(
            ticket,
            ticket.NextCycleNumber(),
            SlaCycleReason.Initial,
            policy,
            now
        );
        db.TicketSlaCycles.Add(cycle);
        db.TicketActivities.Add(
            ticket.Record(
                TicketActivityType.SlaCycleStarted,
                null,
                now,
                newValue: "1",
                relatedId: cycle.Id
            )
        );

        foreach (var membershipId in assignees)
        {
            db.TicketAssignments.Add(
                TicketAssignment.Create(
                    ticket.TenantId,
                    ticket.Id,
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

        if (assignees.Count > 0)
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

        // Notifications persist in the same transaction; the live push happens after commit.
        await notifications.StageAsync(
            ticket,
            NotificationType.TicketCreated,
            actor.UserId,
            $"created:{ticket.Id}",
            toStaff: false,
            toCustomer: false, // a customer is never told about a ticket creation
            alsoUsers: await audience.WorkspaceStaffAsync(ct), // active Owners, Managers and Agents; the actor is removed by the service
            ct: ct
        );
        if (assignees.Count > 0)
        {
            var assigneeUsers = await db
                .Memberships.AsNoTracking()
                .Where(m => assignees.Contains(m.Id))
                .Select(m => m.UserId)
                .ToListAsync(ct);
            await notifications.StageAsync(
                ticket,
                NotificationType.TicketAssigned,
                actor.UserId,
                $"assigned:{ticket.Id}:create",
                toStaff: false,
                toCustomer: false,
                alsoUsers: assigneeUsers,
                ct: ct
            );
        }

        outbox.StageTicketChanged(ticket.TenantId, ticket.Id, TicketChanges.Created);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        LogCreated(logger, ticket.Id, ticket.TenantId);
        return await GetAsync(ticket.Id, ct);
    }

    // The UPDATE takes a row lock on the tenant counter that lasts until the surrounding transaction commits, so concurrent
    // creators are serialized and can never receive the same number (and a failed creation leaves no gap).
    /// <returns>The next number, or null when numbering was never initialized for the tenant.</returns>
    private async Task<long?> NextNumberAsync(CancellationToken ct)
    {
        var rows = await db.TicketCounters.ExecuteUpdateAsync(
            s => s.SetProperty(c => c.LastNumber, c => c.LastNumber + 1),
            ct
        );
        if (rows != 1)
        {
            return null;
        }

        return await db.TicketCounters.Select(c => c.LastNumber).SingleAsync(ct);
    }

    // ---------------------------------------------------------------- read

    public async Task<TicketDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        var now = Now;
        var row =
            await Project(access.Visible(actor).Where(t => t.Id == id), actor.MembershipId).SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        var prefix = await PrefixAsync(ct);
        var meta = await db
            .Tickets.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Description, t.CreatedByUserId })
            .SingleAsync(ct);

        IReadOnlyList<AssigneeDto>? assignees = null;
        SlaCycleDto? sla = null;
        if (!actor.IsCustomer)
        {
            assignees = await (
                from a in db.TicketAssignments.AsNoTracking()
                join m in db.Memberships on a.MembershipId equals m.Id
                join u in db.Users on m.UserId equals u.Id
                where a.TicketId == id && a.UnassignedAtUtc == null
                orderby a.AssignedAtUtc, a.Id
                select new AssigneeDto(
                    m.Id,
                    u.Id,
                    u.DisplayName,
                    a.AssignedAtUtc,
                    m.Role == TenantRole.Agent
                        && m.Status == MembershipStatus.Active
                        && u.Status == Domain.Users.UserStatus.Active
                )
            ).ToListAsync(ct);
            var current = await db
                .TicketSlaCycles.AsNoTracking()
                .Where(c => c.TicketId == id)
                .OrderByDescending(c => c.CycleNumber)
                .FirstOrDefaultAsync(ct);
            sla = current is null ? null : ToDto(current, now);
        }

        return new TicketDetailDto(
            ToSummary(row, prefix, now, !actor.IsCustomer),
            meta.Description,
            actor.IsCustomer ? null : meta.CreatedByUserId,
            assignees,
            sla
        ); // the staff user id is never sent to customers
    }

    public async Task<PagedResult<TicketSummaryDto>> ListAsync(
        TicketListFilter filter,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
        if (
            actor.IsCustomer
            && (
                filter.AssignedMembershipId is not null
                || filter.AssignedToMe
                || filter.CustomerId is not null
                || filter.State is TicketState.Overdue or TicketState.Unstaffed
            )
        )
        {
            throw AppException.Forbidden(
                "Customers cannot filter by assignee, customer or SLA state."
            );
        }

        var now = Now;
        var (page, pageSize, skip) = Paging.Normalize(filter.Page, filter.PageSize, MaxPageSize); // overflow-safe paging
        var effective = access.EffectiveAssignments();

        // Authorization is applied FIRST, inside the query; every other filter only narrows what the caller may already see.
        var query = access.Visible(actor);
        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var wanted = statuses.ToArray();
            query = query.Where(t => wanted.Contains(t.Status));
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(t => t.Priority == priority);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(t => t.CategoryId == categoryId);
        }

        if (filter.CustomerId is { } customerId)
        {
            query = query.Where(t => t.CustomerId == customerId);
        }

        if (filter.AssignedToMe)
        {
            var me = actor.MembershipId; // the caller's own membership, resolved server-side
            query = query.Where(t =>
                db.TicketAssignments.Any(a =>
                    a.TicketId == t.Id && a.MembershipId == me && a.UnassignedAtUtc == null
                )
            );
        }

        if (filter.AssignedMembershipId is { } assignee)
        {
            query = query.Where(t =>
                db.TicketAssignments.Any(a =>
                    a.TicketId == t.Id && a.MembershipId == assignee && a.UnassignedAtUtc == null
                )
            );
        }

        if (filter.CreatedFrom is { } from)
        {
            query = query.Where(t => t.CreatedAtUtc >= from);
        }

        if (filter.CreatedTo is { } to)
        {
            query = query.Where(t => t.CreatedAtUtc <= to);
        }

        query = filter.State switch
        {
            TicketState.Open => query.Where(t =>
                t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed
            ),
            TicketState.Finished => query.Where(t =>
                t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed
            ),
            TicketState.Overdue => query.Where(t =>
                t.Status != TicketStatus.Resolved
                && t.Status != TicketStatus.Closed
                && db.TicketSlaCycles.Any(c =>
                    c.TicketId == t.Id && c.ResolvedAtUtc == null && c.ResolutionDueAtUtc < now
                )
            ),
            TicketState.Unstaffed => query.Where(t =>
                t.Status != TicketStatus.Resolved
                && t.Status != TicketStatus.Closed
                && !effective.Any(a => a.TicketId == t.Id)
            ),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var lower = term.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(..) LIKE; culture/StringComparison overloads cannot be translated
            query = TryParseNumber(term) is { } number
                ? query.Where(t => t.Number == number || t.Title.ToLower().Contains(lower))
                : query.Where(t => t.Title.ToLower().Contains(lower));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(ct);
        var rows = await Project(
                Order(query, filter.SortBy, filter.Descending).Skip(skip).Take(pageSize),
                actor.MembershipId
            )
            .ToListAsync(ct);
        var prefix = await PrefixAsync(ct);
        var assignees = actor.IsCustomer ? null : await AssigneesOfAsync(rows.ConvertAll(r => r.Id), ct);
        return new PagedResult<TicketSummaryDto>(
            rows.ConvertAll(r =>
                ToSummary(r, prefix, now, !actor.IsCustomer) with
                {
                    Assignees = assignees is null ? null : assignees.GetValueOrDefault(r.Id) ?? [],
                }
            ),
            page,
            pageSize,
            total
        );
    }

    public async Task<PagedResult<TicketActivityDto>> GetActivityAsync(
        Guid ticketId,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw NotFound();
        }

        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var customer = actor.IsCustomer;
        // Internal entries (staffing, child tasks, attachments, SLA) are for people responsible for the ticket; others get the lifecycle.
        var lifecycleOnly = customer || !await access.MayWorkOnAsync(actor, ticketId, ct);
        var query = db
            .TicketActivities.AsNoTracking()
            .Where(a => a.TicketId == ticketId && (!lifecycleOnly || a.CustomerVisible));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(a => a.Sequence).Skip(skip).Take(pageSize).ToListAsync(ct);

        // Names for the whole page in two queries (never one per entry). The audit row keeps the user id, so a deactivated or removed
        // member is still named.
        var actorIds = rows.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!.Value).Distinct().ToList();
        Dictionary<Guid, string> actorNames = actorIds.Count == 0
            ? new()
            : await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var assignedIds = rows
            .Where(a => a.Type is TicketActivityType.AgentAssigned or TicketActivityType.AgentUnassigned && a.RelatedId is not null)
            .Select(a => a.RelatedId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, string> assignedNames = customer || assignedIds.Count == 0
            ? new()
            : await (
                from m in db.Memberships.AsNoTracking()
                join u in db.Users on m.UserId equals u.Id
                where assignedIds.Contains(m.Id)
                select new { m.Id, u.DisplayName }
            ).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);

        var items = rows.ConvertAll(a =>
        {
            var own = a.ActorUserId == actor.UserId;
            var name = a.ActorUserId is { } id && (!customer || own) ? actorNames.GetValueOrDefault(id) : null;
            return new TicketActivityDto(
                a.Id,
                a.Sequence,
                a.Type,
                a.OccurredAtUtc,
                customer ? null : a.ActorUserId,
                a.OldValue,
                a.NewValue,
                name,
                a.RelatedId is { } related && assignedNames.TryGetValue(related, out var assigned) ? assigned : null
            );
        });
        return new PagedResult<TicketActivityDto>(items, page, pageSize, total);
    }

    /// <summary>
    /// Mirrors the rule enforced by <see cref="CreateAsync"/>: staff may always open tickets, a Customer only through an ACTIVE contact
    /// linked to their own user. Display only; creation re-checks everything.
    /// </summary>
    public async Task<TicketCreationEligibilityDto> GetCreationEligibilityAsync(CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        if (!actor.IsCustomer)
        {
            return new TicketCreationEligibilityDto(true, null);
        }

        var active = await db
            .Customers.AsNoTracking()
            .Where(c => c.UserId == actor.UserId)
            .Select(c => (bool?)c.IsActive)
            .SingleOrDefaultAsync(ct);
        return active switch
        {
            null => new TicketCreationEligibilityDto(false, "NotLinked"),
            false => new TicketCreationEligibilityDto(false, "Inactive"),
            _ => new TicketCreationEligibilityDto(true, null),
        };
    }

    public async Task<TicketSlaDto> GetSlaAsync(Guid ticketId, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Customers cannot view SLA details.");
        }

        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw NotFound();
        }

        var now = Now;
        var cycles = (
            await db
                .TicketSlaCycles.AsNoTracking()
                .Where(c => c.TicketId == ticketId)
                .OrderBy(c => c.CycleNumber)
                .ToListAsync(ct)
        ).ConvertAll(c => ToDto(c, now));
        return new TicketSlaDto(cycles.LastOrDefault(), cycles);
    }

    // ---------------------------------------------------------------- update

    /// <summary>
    /// Edits only the allowed fields (title, description, priority, category); ownership, requester, number and status can never be
    /// changed here. A priority change re-applies the new policy to the ACTIVE SLA cycle only.
    /// </summary>
    public async Task<ActionResult<TicketDetailDto>> UpdateAsync(
        Guid id,
        UpdateTicketCommand cmd,
        CancellationToken ct
    )
    {
        // Edits also run in an authorized transaction.
        if (await access.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction;
        var ticket = await access.Visible(actor).Where(t => t.Id == id).SingleOrDefaultAsync(ct);
        if (ticket is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Ticket not found." });
        }

        if (!await access.MayWorkOnAsync(actor, id, ct))
        {
            return ActorAccess.Forbidden(TicketAccess.AgentMustBeAssigned);
        }

        if (cmd.Version is { } expected && expected != ticket.Version)
        {
            return ChangedByOthers();
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "A closed ticket cannot be edited; reopen it first." }
            );
        }

        if (
            actor.IsCustomer
            && (
                ticket.Status != TicketStatus.Open
                || cmd.Priority is not null
                || cmd.CategoryId is not null
            )
        )
        {
            return ActorAccess.Forbidden(
                "Customers can only edit the title and description of their open tickets."
            );
        }

        var now = Now;
        var changed = false;
        var newTitle = cmd.Title?.Trim() ?? ticket.Title;
        var newDescription = cmd.Description?.Trim() ?? ticket.Description;
        if (newTitle != ticket.Title || newDescription != ticket.Description)
        {
            var fields = string.Join(
                ',',
                new[]
                {
                    newTitle != ticket.Title ? "title" : null,
                    newDescription != ticket.Description ? "description" : null,
                }.Where(f => f is not null)
            );
            ticket.UpdateDetails(newTitle, newDescription, now); // content is never copied into the timeline
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.DetailsUpdated,
                    actor.UserId,
                    now,
                    newValue: fields
                )
            );
            changed = true;
        }

        if (cmd.Priority is { } priority && priority != ticket.Priority)
        {
            var old = ticket.Priority;
            ticket.ChangePriority(priority, now);
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.PriorityChanged,
                    actor.UserId,
                    now,
                    old.ToString(),
                    priority.ToString()
                )
            );
            if (await ReapplyPolicyAsync(ticket.Id, priority, ct) is { } policyFailure)
            {
                return policyFailure;
            }

            changed = true;
        }

        if (cmd.CategoryId is { } categoryId && categoryId != ticket.CategoryId)
        {
            if (!await db.TicketCategories.AnyAsync(c => c.Id == categoryId && c.IsActive, ct))
            {
                return CategoryUnavailable();
            }

            var old = ticket.CategoryId;
            ticket.ChangeCategory(categoryId, now);
            db.TicketActivities.Add(
                ticket.Record(
                    TicketActivityType.CategoryChanged,
                    actor.UserId,
                    now,
                    old.ToString(),
                    categoryId.ToString()
                )
            );
            changed = true;
        }

        if (changed)
        {
            outbox.StageTicketChanged(ticket.TenantId, ticket.Id, TicketChanges.Updated);
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ChangedByOthers();
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
            {
                return ChangedByOthers();
            }
            catch (Exception ex) when (db.IsDeadlock(ex)) // a deadlock victim is a conflict too
            {
                return ChangedByOthers();
            }
        }

        return await GetAsync(id, ct);
    }

    /// <returns>Null when applied; the 409 when the new priority has no active SLA policy.</returns>
    private async Task<ObjectResult?> ReapplyPolicyAsync(
        Guid ticketId,
        TicketPriority priority,
        CancellationToken ct
    )
    {
        var cycle = await db.TicketSlaCycles.SingleOrDefaultAsync(
            c => c.TicketId == ticketId && c.ResolvedAtUtc == null,
            ct
        );
        if (cycle is null)
        {
            return null;
        }

        var policy = await db
            .SlaPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Priority == priority && p.IsActive, ct);
        if (policy is null)
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "No active SLA policy exists for this priority." }
            );
        }

        cycle.ReapplyPolicy(policy);
        return null;
    }

    // ---------------------------------------------------------------- projections

    internal sealed record TicketRow(
        Guid Id,
        long Number,
        string Title,
        TicketPriority Priority,
        TicketStatus Status,
        Guid CategoryId,
        string? CategoryName,
        Guid? CustomerId,
        string? CustomerName,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        DateTime? ResolvedAtUtc,
        int Version,
        int AssigneeCount,
        DateTime? ResolutionDueAtUtc,
        int EligibleAssigneeCount,
        bool AssignedToMe
    );

    // One query per page: correlated sub-selects instead of Include chains or per-row follow-up queries.
    private IQueryable<TicketRow> Project(IQueryable<Ticket> query, Guid actorMembershipId)
    {
        var effective = access.EffectiveAssignments();
        return query.Select(t => new TicketRow(
            t.Id,
            t.Number,
            t.Title,
            t.Priority,
            t.Status,
            t.CategoryId,
            db.TicketCategories.Where(c => c.Id == t.CategoryId)
                .Select(c => c.Name)
                .FirstOrDefault(),
            t.CustomerId,
            db.Customers.Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefault(),
            t.CreatedAtUtc,
            t.UpdatedAtUtc,
            t.ResolvedAtUtc,
            t.Version,
            db.TicketAssignments.Count(a => a.TicketId == t.Id && a.UnassignedAtUtc == null),
            db.TicketSlaCycles.Where(c => c.TicketId == t.Id && c.ResolvedAtUtc == null)
                .Select(c => (DateTime?)c.ResolutionDueAtUtc)
                .FirstOrDefault(),
            effective.Count(a => a.TicketId == t.Id),
            db.TicketAssignments.Any(a =>
                a.TicketId == t.Id && a.MembershipId == actorMembershipId && a.UnassignedAtUtc == null
            )
        ));
    }

    /// <summary>The current assignees of every ticket on a page in ONE query, so board cards need no per-card request.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<AssigneeDto>>> AssigneesOfAsync(
        List<Guid> ticketIds,
        CancellationToken ct
    )
    {
        var rows = await (
            from a in db.TicketAssignments.AsNoTracking()
            join m in db.Memberships on a.MembershipId equals m.Id
            join u in db.Users on m.UserId equals u.Id
            where ticketIds.Contains(a.TicketId) && a.UnassignedAtUtc == null
            orderby a.AssignedAtUtc, a.Id
            select new
            {
                a.TicketId,
                Dto = new AssigneeDto(
                    m.Id,
                    u.Id,
                    u.DisplayName,
                    a.AssignedAtUtc,
                    m.Role == TenantRole.Agent
                        && m.Status == MembershipStatus.Active
                        && u.Status == Domain.Users.UserStatus.Active
                ),
            }
        ).ToListAsync(ct);
        return rows
            .GroupBy(r => r.TicketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssigneeDto>)g.Select(r => r.Dto).ToList());
    }

    private async Task<string> PrefixAsync(CancellationToken ct) =>
        await db.Settings.AsNoTracking().Select(s => s.TicketNumberPrefix).SingleOrDefaultAsync(ct)
        ?? TenantSettings.DefaultTicketPrefix;

    private static TicketSummaryDto ToSummary(
        TicketRow r,
        string prefix,
        DateTime now,
        bool staff
    ) =>
        new(
            r.Id,
            r.Number,
            TenantSettings.FormatNumber(prefix, r.Number),
            r.Title,
            r.Priority,
            r.Status,
            r.CategoryId,
            r.CategoryName ?? string.Empty,
            r.CustomerId,
            r.CustomerName,
            r.CreatedAtUtc,
            r.UpdatedAtUtc,
            r.ResolvedAtUtc,
            r.Version,
            staff ? r.AssigneeCount : null,
            staff ? r.EligibleAssigneeCount : null,
            staff ? r.ResolutionDueAtUtc : null,
            staff
                ? r.ResolutionDueAtUtc is { } due
                    && !TicketWorkflow.IsFinished(r.Status)
                    && now > due
                : null,
            staff ? r.AssignedToMe : null
        );

    internal static SlaCycleDto ToDto(TicketSlaCycle c, DateTime now) =>
        new(
            c.Id,
            c.CycleNumber,
            c.Reason,
            c.StartedAtUtc,
            c.ResponseMinutes,
            c.ResolutionMinutes,
            c.ResponseDueAtUtc,
            c.ResolutionDueAtUtc,
            c.FirstResponseAtUtc,
            c.ResolvedAtUtc,
            SlaEvaluation.Response(c, now),
            SlaEvaluation.Resolution(c, now),
            SlaEvaluation.RemainingResolutionMinutes(c, now)
        );

    // Whitelisted sort fields; the unique per-tenant Number is the deterministic tie-breaker.
    private static IOrderedQueryable<Ticket> Order(IQueryable<Ticket> q, string? sortBy, bool desc)
    {
        IOrderedQueryable<Ticket> ordered = sortBy?.ToLowerInvariant() switch
        {
            null or "" or "createdat" => desc
                ? q.OrderByDescending(t => t.CreatedAtUtc)
                : q.OrderBy(t => t.CreatedAtUtc),
            "updatedat" => desc
                ? q.OrderByDescending(t => t.UpdatedAtUtc)
                : q.OrderBy(t => t.UpdatedAtUtc),
            "number" => desc ? q.OrderByDescending(t => t.Number) : q.OrderBy(t => t.Number),
            "title" => desc ? q.OrderByDescending(t => t.Title) : q.OrderBy(t => t.Title),
            "priority" => desc
                ? q.OrderByDescending(t =>
                    t.Priority == TicketPriority.Urgent ? 3
                    : t.Priority == TicketPriority.High ? 2
                    : t.Priority == TicketPriority.Medium ? 1
                    : 0
                )
                : q.OrderBy(t =>
                    t.Priority == TicketPriority.Urgent ? 3
                    : t.Priority == TicketPriority.High ? 2
                    : t.Priority == TicketPriority.Medium ? 1
                    : 0
                ),
            "status" => desc
                ? q.OrderByDescending(t =>
                    t.Status == TicketStatus.Closed ? 5
                    : t.Status == TicketStatus.Resolved ? 4
                    : t.Status == TicketStatus.OnHold ? 3
                    : t.Status == TicketStatus.InProgress ? 2
                    : t.Status == TicketStatus.Assigned ? 1
                    : 0
                )
                : q.OrderBy(t =>
                    t.Status == TicketStatus.Closed ? 5
                    : t.Status == TicketStatus.Resolved ? 4
                    : t.Status == TicketStatus.OnHold ? 3
                    : t.Status == TicketStatus.InProgress ? 2
                    : t.Status == TicketStatus.Assigned ? 1
                    : 0
                ),
            _ => throw AppException.BadRequest("Unsupported sort field."),
        };
        return ordered.ThenBy(t => t.Number);
    }

    private static long? TryParseNumber(string term)
    {
        var match = NumberPattern().Match(term);
        return match.Success && long.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    [GeneratedRegex(@"^(?:[A-Za-z]{1,10}-)?(\d{1,18})$")]
    private static partial Regex NumberPattern();

    private static AppException NotFound() => AppException.NotFound("Ticket not found.");

    private static BadRequestObjectResult CategoryUnavailable() =>
        new(new ProblemDetails { Title = "The category is not available." });

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
        Message = "Ticket {TicketId} created in tenant {TenantId}"
    )]
    private static partial void LogCreated(ILogger logger, Guid ticketId, Guid tenantId);
}
