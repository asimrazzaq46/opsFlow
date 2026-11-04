using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Memberships;

public sealed partial class MembershipService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    IUserDirectory users,
    TimeProvider clock,
    ILogger<MembershipService> logger
)
{
    private const int MaxAttempts = 3;
    private const int MaxPageSize = 100;

    public async Task<PagedResult<MemberDto>> ListAsync(
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
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
                u?.Email ?? string.Empty,
                u?.DisplayName ?? string.Empty,
                m.Role,
                m.Status == MembershipStatus.Active,
                m.CreatedAtUtc
            );
        });
        return new PagedResult<MemberDto>(items, page, pageSize, total);
    }

    /// <returns>Null when the change was applied; otherwise the failure to answer with.</returns>
    public Task<ObjectResult?> ChangeRoleAsync(Guid membershipId, TenantRole role, CancellationToken ct)
    {
        if (!role.IsStaff())
        {
            return Task.FromResult<ObjectResult?>(
                new BadRequestObjectResult(new ProblemDetails { Title = "Only staff roles can be assigned." })
            );
        }

        return UpdateAsync(
            membershipId,
            m => m.ChangeRole(role, clock.GetUtcNow().UtcDateTime),
            ct
        );
    }

    public Task<ObjectResult?> SetActiveAsync(Guid membershipId, bool isActive, CancellationToken ct) =>
        UpdateAsync(membershipId, m => m.SetActive(isActive, clock.GetUtcNow().UtcDateTime), ct);

    /// <summary>
    /// Applies a change and keeps at least one USABLE Owner (active membership AND active user). EVERY membership mutation
    /// rotates the tenant OwnerChangeStamp, which was read BEFORE the actor/owner checks, so any concurrent change to the
    /// actor's or an Owner's authorization (demotion, suspension, deactivation) makes this commit fail; the retry then
    /// re-reads the actor's CURRENT authorization and the Owner count against fresh data. Rejected attempts roll back fully.
    /// </summary>
    private async Task<ObjectResult?> UpdateAsync(
        Guid membershipId,
        Action<Membership> change,
        CancellationToken ct
    )
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
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

            tenant.RotateOwnerChangeStamp();

            try
            {
                await db.SaveChangesAsync(ct);
                LogChanged(logger, member.TenantId, member.Id, member.Role, member.Status);
                return null;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        return new ConflictObjectResult(
            new ProblemDetails
            {
                Title = "The membership was changed concurrently.",
                Detail = "Please retry.",
            }
        );
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
