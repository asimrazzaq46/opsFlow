using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Team;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Memberships;

/// <summary>
/// The workspace's employee directory, profiles, skills and work statistics. Everything is tenant-scoped by the query filters and decided on
/// the caller's CURRENT database role: Owners administer every staff member, Managers administer Agents, Agents see the directory without
/// other people's emails and only their own detail, Customers get nothing. Role and status changes stay in <see cref="MembershipService"/>.
/// </summary>
public sealed class TeamService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    TimeProvider clock
)
{
    private const int MaxPageSize = 100;
    private const int MaxListed = 25;
    private const int MaxSkillsPerMember = 30;

    // ---------------------------------------------------------------- reads (GraphQL)

    public async Task<PagedResult<TeamMemberRowDto>> DirectoryAsync(
        string? search,
        TenantRole? role,
        bool? isActive,
        Guid? skillId,
        string? sortBy,
        bool descending,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var caller = await RequireStaffAsync(ct);
        var admin = IsAdmin(caller);
        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);

        var query =
            from m in db.Memberships
            where m.Role != TenantRole.Customer
            join u in db.Users on m.UserId equals u.Id
            select new
            {
                m.Id,
                m.UserId,
                m.Role,
                m.Status,
                m.CreatedAtUtc,
                u.DisplayName,
                u.Email,
            };
        if (role is { } r && r.IsStaff())
        {
            query = query.Where(x => x.Role == r);
        }

        if (isActive is { } active)
        {
            var status = active ? MembershipStatus.Active : MembershipStatus.Inactive;
            query = query.Where(x => x.Status == status);
        }

        if (skillId is { } skill)
        {
            query = query.Where(x => db.MemberSkills.Any(s => s.MembershipId == x.Id && s.SkillId == skill));
        }

        if (search?.Trim().ToLowerInvariant() is { Length: > 0 } term)
        {
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(..) LIKE; culture/StringComparison overloads cannot be translated
            // Only administrators may search by email: for Agents a match would reveal addresses they cannot see.
            query = admin
                ? query.Where(x => x.DisplayName.ToLower().Contains(term) || x.Email.ToLower().Contains(term))
                : query.Where(x => x.DisplayName.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(ct);
        var ordered = (sortBy ?? "name").ToLowerInvariant() switch
        {
            "role" => descending ? query.OrderByDescending(x => x.Role) : query.OrderBy(x => x.Role),
            "status" => descending ? query.OrderByDescending(x => x.Status) : query.OrderBy(x => x.Status),
            "joined" => descending
                ? query.OrderByDescending(x => x.CreatedAtUtc)
                : query.OrderBy(x => x.CreatedAtUtc),
            _ => descending ? query.OrderByDescending(x => x.DisplayName) : query.OrderBy(x => x.DisplayName),
        };
        var rows = await ordered.ThenBy(x => x.Id).Skip(skip).Take(pageSize).ToListAsync(ct);

        var ids = rows.ConvertAll(x => x.Id);
        var skills = await SkillsOfAsync(ids, ct);
        var profiles = await db
            .MemberProfiles.AsNoTracking()
            .Where(p => ids.Contains(p.MembershipId))
            .ToDictionaryAsync(p => p.MembershipId, ct);
        var open = await OpenAssignmentCountsAsync(ids, ct);
        var items = rows.ConvertAll(x =>
        {
            profiles.TryGetValue(x.Id, out var profile);
            return new TeamMemberRowDto(
                x.Id,
                x.UserId,
                x.DisplayName,
                admin || x.Id == tenantContext.MembershipId ? x.Email : null,
                x.Role,
                x.Status == MembershipStatus.Active,
                profile?.JobTitle,
                profile?.Department,
                skills.GetValueOrDefault(x.Id) ?? [],
                open.GetValueOrDefault(x.Id),
                x.CreatedAtUtc
            );
        });
        return new PagedResult<TeamMemberRowDto>(items, page, pageSize, total);
    }

    public async Task<TeamMemberDetailDto> DetailAsync(Guid membershipId, CancellationToken ct)
    {
        var caller = await RequireStaffAsync(ct);
        var self = membershipId == tenantContext.MembershipId;
        if (!IsAdmin(caller) && !self)
        {
            throw AppException.Forbidden("Agents can only open their own employee profile.");
        }

        var member =
            await (
                from ms in db.Memberships.AsNoTracking()
                where ms.Id == membershipId && ms.Role != TenantRole.Customer
                join u in db.Users on ms.UserId equals u.Id
                select new { Membership = ms, u.DisplayName, u.Email }
            ).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Member not found.");

        var m = member.Membership;
        var profile = await db.MemberProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.MembershipId == m.Id, ct);
        var prefix = await PrefixAsync(ct);
        var assigned = await AssignedTicketsQuery(m.Id)
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(MaxListed)
            .Select(t => new { t.Id, t.Number, t.Title, t.Status, t.Priority })
            .ToListAsync(ct);
        var stats = new TeamMemberStatsDto(
            await AssignedTicketsQuery(m.Id).CountAsync(ct),
            await db.TicketTasks.CountAsync(
                x =>
                    x.AssigneeMembershipId == m.Id
                    && x.Status != TicketTaskStatus.Done
                    && ActiveTickets().Any(t => t.Id == x.TicketId),
                ct
            ),
            await ResolvedBy(m.UserId).CountAsync(ct),
            await ResolvedBy(m.UserId).Select(a => a.TicketId).Distinct().CountAsync(ct)
        );
        var activity = await (
            from a in db.TicketActivities.AsNoTracking()
            where a.ActorUserId == m.UserId
            join t in db.Tickets on a.TicketId equals t.Id
            orderby a.OccurredAtUtc descending
            select new { a.TicketId, t.Number, a.Type, a.OccurredAtUtc }
        )
            .Take(MaxListed)
            .ToListAsync(ct);

        var row = new TeamMemberRowDto(
            m.Id,
            m.UserId,
            member.DisplayName,
            member.Email, // visible here: the caller is an administrator or the member themselves
            m.Role,
            m.Status == MembershipStatus.Active,
            profile?.JobTitle,
            profile?.Department,
            (await SkillsOfAsync([m.Id], ct)).GetValueOrDefault(m.Id) ?? [],
            stats.OpenAssignmentCount,
            m.CreatedAtUtc
        );
        return new TeamMemberDetailDto(
            row,
            profile?.Phone,
            profile?.UpdatedAtUtc,
            MayAdminister(caller, m.Role),
            caller == TenantRole.TenantOwner,
            stats,
            assigned.ConvertAll(t => new AssignedTicketDto(
                t.Id,
                TenantSettings.FormatNumber(prefix, t.Number),
                t.Title,
                t.Status,
                t.Priority
            )),
            activity.ConvertAll(a => new MemberActivityDto(
                a.TicketId,
                TenantSettings.FormatNumber(prefix, a.Number),
                a.Type,
                a.OccurredAtUtc
            )),
            IsAdmin(caller) ? await AdminHistoryAsync(m.Id, ct) : null
        );
    }

    public async Task<IReadOnlyList<SkillDto>> SkillsAsync(bool includeInactive, CancellationToken ct)
    {
        await RequireStaffAsync(ct);
        return await db
            .Skills.AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new SkillDto(s.Id, s.Name, s.IsActive))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- writes (REST)

    public async Task<ObjectResult?> UpdateProfileAsync(
        Guid membershipId,
        UpdateMemberProfileCommand cmd,
        CancellationToken ct
    )
    {
        if (await actor.TryBeginAuthorizedAsync([membershipId], ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction;
        if (await AdministeredMemberAsync(membershipId, role, ct) is not { } target)
        {
            return await RefusalAsync(membershipId, role, ct);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var profile = await db.MemberProfiles.SingleOrDefaultAsync(p => p.MembershipId == target.Id, ct);
        if (cmd.CheckVersion && !SameVersion(profile?.UpdatedAtUtc, cmd.ExpectedUpdatedAtUtc))
        {
            return Stale();
        }

        if (profile is null)
        {
            profile = MemberProfile.Create(target.TenantId, target.Id, now);
            db.MemberProfiles.Add(profile);
        }

        profile.Update(cmd.JobTitle, cmd.Department, cmd.Phone, now);
        Audit(target.TenantId, AuditAction.MemberProfileUpdated, target.Id, null, null, now); // values may be personal: not copied
        return await CommitAsync(transaction, UniqueConstraintKind.MemberProfile, ct);
    }

    /// <param name="expectedSkillIds">The set the editor saw; when given and no longer current, the replacement is refused.</param>
    public async Task<ObjectResult?> SetMemberSkillsAsync(
        Guid membershipId,
        IReadOnlyCollection<Guid> skillIds,
        IReadOnlyCollection<Guid>? expectedSkillIds,
        CancellationToken ct
    )
    {
        var wanted = skillIds.Distinct().ToList();
        if (wanted.Count > MaxSkillsPerMember)
        {
            return new BadRequestObjectResult(new ProblemDetails { Title = $"A member can have at most {MaxSkillsPerMember} skills." });
        }

        if (await actor.TryBeginAuthorizedAsync([membershipId], ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction;
        if (await AdministeredMemberAsync(membershipId, role, ct) is not { } target)
        {
            return await RefusalAsync(membershipId, role, ct);
        }

        var current = await db.MemberSkills.Where(s => s.MembershipId == target.Id).ToListAsync(ct);
        if (expectedSkillIds is not null && !current.Select(s => s.SkillId).ToHashSet().SetEquals(expectedSkillIds))
        {
            return Stale();
        }
        var added = wanted.Except(current.Select(s => s.SkillId)).ToList();
        // A deactivated tag may stay on a member but cannot be newly given.
        var assignable = await db.Skills.CountAsync(s => added.Contains(s.Id) && s.IsActive, ct);
        if (assignable != added.Count)
        {
            return new BadRequestObjectResult(new ProblemDetails { Title = "Unknown or inactive skill." });
        }

        var removed = current.Where(s => !wanted.Contains(s.SkillId)).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        db.MemberSkills.RemoveRange(removed);
        db.MemberSkills.AddRange(added.ConvertAll(id => MemberSkill.Create(target.TenantId, target.Id, id, now)));
        var names = await db.Skills.Where(s => wanted.Contains(s.Id)).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);
        Audit(target.TenantId, AuditAction.MemberSkillsChanged, target.Id, null, string.Join(", ", names), now);
        return await CommitAsync(transaction, UniqueConstraintKind.MemberSkill, ct);
    }

    public async Task<ActionResult<SkillDto>> CreateSkillAsync(string name, CancellationToken ct)
    {
        if (InvalidSkillName(name) is { } invalid)
        {
            return invalid;
        }

        if (await BeginCatalogWriteAsync(ct) is not { } transaction)
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage skills.");
        }

        await using var _ = transaction;
        var now = clock.GetUtcNow().UtcDateTime;
        var skill = Skill.Create(tenantContext.TenantId!.Value, name, now);
        db.Skills.Add(skill);
        Audit(skill.TenantId, AuditAction.SkillCreated, null, skill.Id, skill.Name, now);
        return await CommitAsync(transaction, UniqueConstraintKind.SkillName, ct) is { } failure
            ? failure
            : new SkillDto(skill.Id, skill.Name, skill.IsActive);
    }

    public async Task<ActionResult<SkillDto>> UpdateSkillAsync(
        Guid skillId,
        string name,
        bool isActive,
        CancellationToken ct
    )
    {
        if (InvalidSkillName(name) is { } invalid)
        {
            return invalid;
        }

        if (await BeginCatalogWriteAsync(ct) is not { } transaction)
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage skills.");
        }

        await using var _ = transaction;
        var skill = await db.Skills.SingleOrDefaultAsync(s => s.Id == skillId, ct);
        if (skill is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Skill not found." });
        }

        var before = $"{skill.Name}/{(skill.IsActive ? "active" : "inactive")}";
        skill.Rename(name);
        skill.SetActive(isActive);
        var after = $"{skill.Name}/{(skill.IsActive ? "active" : "inactive")}";
        if (before != after)
        {
            Audit(skill.TenantId, AuditAction.SkillUpdated, null, skill.Id, $"{before} -> {after}", clock.GetUtcNow().UtcDateTime);
            if (await CommitAsync(transaction, UniqueConstraintKind.SkillName, ct) is { } failure)
            {
                return failure;
            }
        }

        return new SkillDto(skill.Id, skill.Name, skill.IsActive);
    }

    // ---------------------------------------------------------------- internals

    // PostgreSQL keeps microseconds, so a value read back can differ from .NET ticks by less than one microsecond.
    private static bool SameVersion(DateTime? stored, DateTime? expected) =>
        stored is null ? expected is null : expected is { } e && Math.Abs((stored.Value - e).Ticks) < 10;

    private static ConflictObjectResult Stale()
    {
        var details = new ProblemDetails { Title = "The member was changed by someone else.", Detail = "Reload and try again." };
        details.Extensions["code"] = MembershipService.StaleCode;
        return new ConflictObjectResult(details);
    }

    // A blank name would give a blank uniqueness key; the database rejects it too (CK_Skills_NormalizedName).
    private static BadRequestObjectResult? InvalidSkillName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > Skill.MaxNameLength
            ? new BadRequestObjectResult(new ProblemDetails { Title = $"A skill name needs 1 to {Skill.MaxNameLength} characters." })
            : null;

    private static bool IsAdmin(TenantRole role) => role is TenantRole.TenantOwner or TenantRole.Manager;

    /// <summary>Owners administer every staff member; Managers only Agents (never Owners, other Managers or themselves).</summary>
    private static bool MayAdminister(TenantRole caller, TenantRole target) =>
        caller == TenantRole.TenantOwner || (caller == TenantRole.Manager && target == TenantRole.Agent);

    private async Task<TenantRole> RequireStaffAsync(CancellationToken ct)
    {
        var role = await actor.RequireActiveRoleAsync(ct);
        return role.IsStaff() ? role : throw AppException.Forbidden("The team directory is not available to customers.");
    }

    private async Task<Membership?> AdministeredMemberAsync(Guid membershipId, TenantRole caller, CancellationToken ct)
    {
        var target = await db.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId && m.Role != TenantRole.Customer, ct);
        return target is not null && MayAdminister(caller, target.Role) ? target : null;
    }

    private async Task<ObjectResult> RefusalAsync(Guid membershipId, TenantRole caller, CancellationToken ct) =>
        IsAdmin(caller) && await db.Memberships.AnyAsync(m => m.Id == membershipId && m.Role != TenantRole.Customer, ct)
            ? ActorAccess.Forbidden("Managers can only manage Agents.")
            : caller.IsStaff()
                ? new NotFoundObjectResult(new ProblemDetails { Title = "Member not found." })
                : ActorAccess.Forbidden("Only Owners and Managers can manage employees.");

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginCatalogWriteAsync(CancellationToken ct)
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return null;
        }

        if (IsAdmin(begun.Role))
        {
            return begun.Transaction;
        }

        await begun.Transaction.DisposeAsync();
        return null;
    }

    private void Audit(Guid tenantId, AuditAction action, Guid? membershipId, Guid? skillId, string? detail, DateTime now) =>
        db.AuditEvents.Add(AuditEvent.Create(tenantId, currentUser.UserId, action, membershipId, skillId, detail, now));

    private async Task<ObjectResult?> CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        UniqueConstraintKind conflict,
        CancellationToken ct
    )
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, conflict))
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = conflict == UniqueConstraintKind.SkillName
                        ? "A skill with this name already exists."
                        : "The member was changed by someone else.",
                    Detail = "Reload and try again.",
                }
            );
        }
    }

    private IQueryable<Ticket> ActiveTickets() =>
        db.Tickets.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);

    private IQueryable<Ticket> AssignedTicketsQuery(Guid membershipId) =>
        ActiveTickets()
            .Where(t =>
                db.TicketAssignments.Any(a =>
                    a.TicketId == t.Id && a.MembershipId == membershipId && a.UnassignedAtUtc == null
                )
            );

    // Attribution follows the recorded actor of each Resolved action, never the assignment list.
    private IQueryable<TicketActivity> ResolvedBy(Guid userId) =>
        db.TicketActivities.Where(a => a.ActorUserId == userId && a.Type == TicketActivityType.Resolved);

    private async Task<Dictionary<Guid, int>> OpenAssignmentCountsAsync(List<Guid> ids, CancellationToken ct) =>
        await db
            .TicketAssignments.Where(a =>
                ids.Contains(a.MembershipId)
                && a.UnassignedAtUtc == null
                && ActiveTickets().Any(t => t.Id == a.TicketId)
            )
            .GroupBy(a => a.MembershipId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task<Dictionary<Guid, IReadOnlyList<SkillDto>>> SkillsOfAsync(List<Guid> ids, CancellationToken ct) =>
        (
            await (
                from ms in db.MemberSkills.AsNoTracking()
                where ids.Contains(ms.MembershipId)
                join s in db.Skills on ms.SkillId equals s.Id
                orderby s.Name
                select new { ms.MembershipId, Dto = new SkillDto(s.Id, s.Name, s.IsActive) }
            ).ToListAsync(ct)
        )
            .GroupBy(x => x.MembershipId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SkillDto>)g.Select(x => x.Dto).ToList());

    private async Task<List<AuditEntryDto>> AdminHistoryAsync(Guid membershipId, CancellationToken ct) =>
        await (
            from e in db.AuditEvents.AsNoTracking()
            where e.TargetMembershipId == membershipId
            join u in db.Users on e.ActorUserId equals u.Id into actors
            from u in actors.DefaultIfEmpty()
            orderby e.OccurredAtUtc descending
            select new AuditEntryDto(e.Action, u == null ? null : u.DisplayName, e.Detail, e.OccurredAtUtc)
        )
            .Take(MaxListed)
            .ToListAsync(ct);

    private async Task<string> PrefixAsync(CancellationToken ct) =>
        await db.Settings.Select(s => s.TicketNumberPrefix).SingleOrDefaultAsync(ct) ?? TenantSettings.DefaultTicketPrefix;
}
