using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>Ticket creation, reading (list/detail/activity/SLA) and field updates. Status changes and assignments live in their own services.</summary>
public sealed partial class TicketService(
    IOpsFlowDbContext db,
    TicketAccess access,
    ITenantContext tenantContext,
    NotificationService notifications,
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
    public async Task<TicketDetailDto> CreateAsync(CreateTicketCommand cmd, CancellationToken ct)
    {
        // UPDATED: actor aur (agar hon to) assignees ki rows lock ho kar transaction shuru; commit tak authorization/eligibility nahi badal sakti
        var (transaction, actor) = await access.BeginAuthorizedAsync(cmd.AssigneeMembershipIds, ct);
        await using var _ = transaction;
        var category =
            await db
                .TicketCategories.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == cmd.CategoryId && c.IsActive, ct)
            ?? throw AppException.BadRequest("The category is not available.");

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
                    throw AppException.Forbidden(
                        "Customers cannot choose the customer, priority or assignees."
                    );
                }

                customerId =
                    await db
                        .Customers.AsNoTracking()
                        .Where(c => c.UserId == actor.UserId && c.IsActive)
                        .Select(c => (Guid?)c.Id)
                        .SingleOrDefaultAsync(ct)
                    ?? throw AppException.Forbidden(
                        "Your account is not linked to an active customer contact."
                    );
                break;
            case TenantRole.Agent:
                if (cmd.CustomerId is not null || cmd.AssigneeMembershipIds is { Count: > 0 })
                {
                    throw AppException.Forbidden("Agents can only create tickets for themselves.");
                }

                assignees = [actor.MembershipId]; // the agent-as-requester rule: self-assigned
                break;
            default:
                if (cmd.CustomerId is { } requested)
                {
                    customerId =
                        await db
                            .Customers.AsNoTracking()
                            .Where(c => c.Id == requested && c.IsActive)
                            .Select(c => (Guid?)c.Id)
                            .SingleOrDefaultAsync(ct)
                        ?? throw AppException.BadRequest("The customer is not available.");
                }

                assignees = cmd.AssigneeMembershipIds is { Count: > 0 } ids
                    ? await access.RequireEligibleAgentsAsync(ids, ct)
                    : [];
                break;
        }

        var policy =
            await db
                .SlaPolicies.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Priority == priority && p.IsActive, ct)
            ?? throw AppException.Conflict(
                "No active SLA policy exists for this priority.",
                "An Owner must initialize the tenant defaults."
            );

        var now = Now;
        var number = await NextNumberAsync(ct);
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

        // UPDATED: notifications usi transaction mein persist hoti hain; live push commit ke baad
        await notifications.StageAsync(
            ticket,
            NotificationType.TicketCreated,
            actor.UserId,
            $"created:{ticket.Id}",
            toStaff: true,
            toCustomer: false,
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

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        LogCreated(logger, ticket.Id, ticket.TenantId);
        return await GetAsync(ticket.Id, ct);
    }

    // The UPDATE takes a row lock on the tenant counter that lasts until the surrounding transaction commits, so concurrent
    // creators are serialized and can never receive the same number (and a failed creation leaves no gap).
    private async Task<long> NextNumberAsync(CancellationToken ct)
    {
        var rows = await db.TicketCounters.ExecuteUpdateAsync(
            s => s.SetProperty(c => c.LastNumber, c => c.LastNumber + 1),
            ct
        );
        if (rows != 1)
        {
            throw AppException.Conflict(
                "Ticket numbering is not initialized for this organization.",
                "An Owner must initialize the tenant defaults."
            );
        }

        return await db.TicketCounters.Select(c => c.LastNumber).SingleAsync(ct);
    }

    // ---------------------------------------------------------------- read

    public async Task<TicketDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        var now = Now;
        var row =
            await Project(access.Visible(actor).Where(t => t.Id == id)).SingleOrDefaultAsync(ct)
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
        ); // UPDATED: staff user id customer ko nahi jata
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
        var (page, pageSize, skip) = Paging.Normalize(filter.Page, filter.PageSize, MaxPageSize); // UPDATED: overflow-safe paging
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
                Order(query, filter.SortBy, filter.Descending).Skip(skip).Take(pageSize)
            )
            .ToListAsync(ct);
        var prefix = await PrefixAsync(ct);
        return new PagedResult<TicketSummaryDto>(
            rows.ConvertAll(r => ToSummary(r, prefix, now, !actor.IsCustomer)),
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
        var query = db
            .TicketActivities.AsNoTracking()
            .Where(a => a.TicketId == ticketId && (!customer || a.CustomerVisible));
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(a => a.Sequence)
            .Skip(skip)
            .Take(pageSize)
            .Select(a => new TicketActivityDto(
                a.Id,
                a.Sequence,
                a.Type,
                a.OccurredAtUtc,
                customer ? null : a.ActorUserId,
                a.OldValue,
                a.NewValue
            ))
            .ToListAsync(ct);
        return new PagedResult<TicketActivityDto>(items, page, pageSize, total);
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
    public async Task<TicketDetailDto> UpdateAsync(
        Guid id,
        UpdateTicketCommand cmd,
        CancellationToken ct
    )
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct); // UPDATED: edit bhi authorized transaction mein
        await using var _ = transaction;
        var ticket =
            await access.Visible(actor).Where(t => t.Id == id).SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        if (cmd.Version is { } expected && expected != ticket.Version)
        {
            throw AppException.Conflict(
                "The ticket was changed by someone else.",
                "Reload the ticket and retry."
            );
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            throw AppException.Conflict("A closed ticket cannot be edited; reopen it first.");
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
            throw AppException.Forbidden(
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
            await ReapplyPolicyAsync(ticket.Id, priority, ct);
            changed = true;
        }

        if (cmd.CategoryId is { } categoryId && categoryId != ticket.CategoryId)
        {
            if (!await db.TicketCategories.AnyAsync(c => c.Id == categoryId && c.IsActive, ct))
            {
                throw AppException.BadRequest("The category is not available.");
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
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Reload the ticket and retry."
                );
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
            {
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Reload the ticket and retry."
                );
            }
            catch (Exception ex) when (db.IsDeadlock(ex)) // UPDATED: deadlock victim bhi conflict
            {
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Reload the ticket and retry."
                );
            }
        }

        return await GetAsync(id, ct);
    }

    private async Task ReapplyPolicyAsync(
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
            return;
        }

        var policy =
            await db
                .SlaPolicies.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Priority == priority && p.IsActive, ct)
            ?? throw AppException.Conflict("No active SLA policy exists for this priority.");
        cycle.ReapplyPolicy(policy);
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
        int EligibleAssigneeCount
    );

    // One query per page: correlated sub-selects instead of Include chains or per-row follow-up queries.
    private IQueryable<TicketRow> Project(IQueryable<Ticket> query)
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
            effective.Count(a => a.TicketId == t.Id)
        ));
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
                : null
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Ticket {TicketId} created in tenant {TenantId}"
    )]
    private static partial void LogCreated(ILogger logger, Guid ticketId, Guid tenantId);
}
