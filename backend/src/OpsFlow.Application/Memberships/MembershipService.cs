using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Team;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Memberships;

public sealed partial class MembershipService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    IUserDirectory users,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    OutboxWriter outbox,
    IOutboxSignal outboxSignal,
    TimeProvider clock,
    ILogger<MembershipService> logger
)
{
    private const int MaxAttempts = 3;
    private const int MaxPageSize = 100;
    private const int MaxListedOpenWork = 20;
    public const string OpenWorkCode = "MEMBER_HAS_OPEN_WORK";
    public const string StaleCode = "MEMBER_CHANGED";

    public async Task<PagedResult<MemberDto>> ListAsync(
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        // Shared with GraphQL, whose policy admits Customers: the staff directory is for staff only.
        var callerRole = await actor.RequireActiveRoleAsync(ct);
        if (callerRole == TenantRole.Customer)
        {
            throw AppException.Forbidden("The team directory is not available to customers.");
        }

        // Same rule as the GraphQL team directory: only Owners and Managers see colleagues' email addresses.
        var seesEmails = callerRole is TenantRole.TenantOwner or TenantRole.Manager;

        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize); // overflow-safe paging

        var query = db.Memberships.Where(m => m.Role != TenantRole.Customer);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        var directory = await users.GetAsync(rows.ConvertAll(m => m.UserId), ct);
        var items = rows.ConvertAll(m =>
        {
            directory.TryGetValue(m.UserId, out var u);
            return new MemberDto(
                m.Id,
                m.UserId,
                seesEmails || m.Id == tenantContext.MembershipId ? u?.Email ?? string.Empty : null,
                u?.DisplayName ?? string.Empty,
                m.Role,
                m.Status == MembershipStatus.Active,
                m.CreatedAtUtc
            );
        });
        return new PagedResult<MemberDto>(items, page, pageSize, total);
    }

    /// <returns>Null when the change was applied; otherwise the failure to answer with.</returns>
    public Task<ObjectResult?> ChangeRoleAsync(
        Guid membershipId,
        TenantRole role,
        CancellationToken ct,
        TenantRole? expectedRole = null
    )
    {
        if (!role.IsStaff())
        {
            return Task.FromResult<ObjectResult?>(
                new BadRequestObjectResult(new ProblemDetails { Title = "Only staff roles can be assigned." })
            );
        }

        return UpdateAsync(
            membershipId,
            expectedRole,
            false,
            AuditAction.MemberRoleChanged,
            m => m.ChangeRole(role, clock.GetUtcNow().UtcDateTime),
            ct
        );
    }

    public Task<ObjectResult?> SetActiveAsync(
        Guid membershipId,
        bool isActive,
        CancellationToken ct,
        TenantRole? expectedRole = null,
        bool acknowledgeOpenWork = false
    ) =>
        UpdateAsync(
            membershipId,
            expectedRole,
            acknowledgeOpenWork,
            AuditAction.MemberStatusChanged,
            m => m.SetActive(isActive, clock.GetUtcNow().UtcDateTime),
            ct
        );

    /// <summary>
    /// Applies a change and keeps at least one USABLE Owner (active membership AND active user). EVERY membership mutation
    /// rotates the tenant OwnerChangeStamp, which was read BEFORE the actor/owner checks, so any concurrent change to the
    /// actor's or an Owner's authorization (demotion, suspension, deactivation) makes this commit fail; the retry then
    /// re-reads the actor's CURRENT authorization and the Owner count against fresh data. Rejected attempts roll back fully.
    /// An active Agent cannot move to another role while holding open work: the assignments and tasks would silently stop counting.
    /// Suspension stays possible immediately (e.g. for a compromised account) but only once the administrator has seen the affected work
    /// and acknowledged it; the work then shows as unstaffed for reassignment.
    /// </summary>
    private async Task<ObjectResult?> UpdateAsync(
        Guid membershipId,
        TenantRole? expectedRole,
        bool acknowledgeOpenWork,
        AuditAction action,
        Action<Membership> change,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await using var transaction = await db.BeginTransactionAsync(ct);
            // Read the stamp first, then the actor's current authorization (membership + user status); every mutation rotates the stamp.
            var tenant = await db.Tenants.SingleAsync(ct);
            if (await actor.CheckOwnerAsync(ct) is { } notOwner)
            {
                return notOwner;
            }

            var member = await db.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId, ct);
            if (member is null)
            {
                return new NotFoundObjectResult(new ProblemDetails { Title = "Member not found." });
            }

            if (!member.Role.IsStaff())
            {
                return new BadRequestObjectResult(
                    new ProblemDetails { Title = "Only staff memberships can be managed here." }
                );
            }

            // The administrator decided on the role they saw; a concurrent change in between must not be overwritten silently.
            if (expectedRole is { } expected && member.Role != expected)
            {
                return Problem(
                    StatusCodesConflict,
                    "The member was changed by someone else.",
                    "Reload the team and try again.",
                    StaleCode
                );
            }

            var before = $"{member.Role}/{member.Status}";
            var wasWorkingAgent = member.Role == TenantRole.Agent && member.Status == MembershipStatus.Active;

            var removesUsableOwner =
                member.IsActiveOwner
                && await db.Users.AnyAsync(
                    u => u.Id == member.UserId && u.Status == UserStatus.Active,
                    ct
                );
            change(member);

            if (removesUsableOwner && !member.IsActiveOwner)
            {
                var otherOwners = await db.UsableOwners().CountAsync(m => m.Id != member.Id, ct);
                if (otherOwners == 0)
                {
                    return new ConflictObjectResult(
                        new ProblemDetails { Title = "A tenant must keep at least one active Owner." }
                    );
                }
            }

            var after = $"{member.Role}/{member.Status}";
            if (before == after)
            {
                return null; // nothing changed: no stamp rotation, audit entry or refresh signal
            }

            tenant.RotateOwnerChangeStamp();
            db.AuditEvents.Add(
                AuditEvent.Create(
                    member.TenantId,
                    currentUser.UserId,
                    action,
                    member.Id,
                    null,
                    $"{before} -> {after}",
                    clock.GetUtcNow().UtcDateTime
                )
            );
            outbox.StageMembershipChanged(member.TenantId, member.Id, member.UserId);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                continue; // the transaction is disposed (rolled back) and the next attempt re-reads everything
            }

            // Counted AFTER the membership UPDATE: it waits for task and assignment writers holding this membership's row lock, so work
            // they commit is seen here, and later writers re-read the new role. Checking before the write would leave a race.
            var leavesAgentRole = member.Role != TenantRole.Agent;
            var suspendsAgent = member.Role == TenantRole.Agent && member.Status != MembershipStatus.Active;
            if (wasWorkingAgent && (leavesAgentRole || (suspendsAgent && !acknowledgeOpenWork))
                && await OpenWorkAsync(member.Id, canAcknowledge: !leavesAgentRole, ct) is { } openWork)
            {
                return openWork;
            }

            await transaction.CommitAsync(ct);
            outboxSignal.Notify();
            LogChanged(logger, member.TenantId, member.Id, member.Role, member.Status);
            return null;
        }

        return new ConflictObjectResult(
            new ProblemDetails
            {
                Title = "The membership was changed concurrently.",
                Detail = "Please retry.",
            }
        );
    }

    private const int StatusCodesConflict = 409;

    /// <summary>
    /// The member's responsibilities that would stop counting: open assignments and unfinished tasks on tickets that are not yet resolved
    /// or closed. Null when there are none; otherwise a 409 listing them so the administrator can reassign first.
    /// </summary>
    private async Task<ObjectResult?> OpenWorkAsync(Guid membershipId, bool canAcknowledge, CancellationToken ct)
    {
        var activeTickets = db.Tickets.Where(t =>
            t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed
        );
        var assignedTickets = activeTickets.Where(t =>
            db.TicketAssignments.Any(a =>
                a.TicketId == t.Id && a.MembershipId == membershipId && a.UnassignedAtUtc == null
            )
        );
        var assignmentCount = await assignedTickets.CountAsync(ct);
        var taskCount = await db.TicketTasks.CountAsync(
            x =>
                x.AssigneeMembershipId == membershipId
                && x.Status != TicketTaskStatus.Done
                && activeTickets.Any(t => t.Id == x.TicketId),
            ct
        );
        if (assignmentCount == 0 && taskCount == 0)
        {
            return null;
        }

        var prefix =
            await db.Settings.Select(s => s.TicketNumberPrefix).SingleOrDefaultAsync(ct)
            ?? TenantSettings.DefaultTicketPrefix;
        var tickets = await assignedTickets
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(MaxListedOpenWork)
            .Select(t => new { t.Id, t.Number, t.Title })
            .ToListAsync(ct);
        var problem = Problem(
            StatusCodesConflict,
            "The member still has open work.",
            canAcknowledge
                ? "Suspending makes this work unstaffed until it is reassigned; repeat with acknowledgeOpenWork to confirm."
                : "Reassign their tickets and tasks before changing their role.",
            OpenWorkCode
        );
        var details = (ProblemDetails)problem.Value!;
        details.Extensions["openAssignmentCount"] = assignmentCount;
        details.Extensions["openTaskCount"] = taskCount;
        details.Extensions["canAcknowledge"] = canAcknowledge;
        details.Extensions["tickets"] = tickets.ConvertAll(t => new OpenWorkTicketDto(
            t.Id,
            TenantSettings.FormatNumber(prefix, t.Number),
            t.Title
        ));
        return problem;
    }

    private static ObjectResult Problem(int status, string title, string detail, string code)
    {
        var details = new ProblemDetails { Status = status, Title = title, Detail = detail };
        details.Extensions["code"] = code;
        return new ObjectResult(details) { StatusCode = status };
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Membership {MembershipId} in tenant {TenantId} is now {Role}/{Status}"
    )]
    private static partial void LogChanged(
        ILogger logger,
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        MembershipStatus status
    );
}

/// <summary>A ticket the member is still responsible for; listed when a role or status change is refused.</summary>
public sealed record OpenWorkTicketDto(Guid TicketId, string DisplayNumber, string Title);
