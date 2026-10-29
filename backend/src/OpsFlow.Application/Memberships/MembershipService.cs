using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Memberships;

public sealed record MemberDto(Guid Id, Guid UserId, string Email, string DisplayName, TenantRole Role, bool IsActive, DateTime CreatedAtUtc);

public sealed partial class MembershipService(IOpsFlowDbContext db, ActorAccess actor, IUserDirectory users, TimeProvider clock, ILogger<MembershipService> logger)
{
    private const int MaxAttempts = 3;
    private const int MaxPageSize = 100;

    public async Task<PagedResult<MemberDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Memberships.Where(m => m.Role != TenantRole.Customer);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var directory = await users.GetAsync(rows.ConvertAll(m => m.UserId), ct);
        var items = rows.ConvertAll(m =>
        {
            directory.TryGetValue(m.UserId, out var u);
            return new MemberDto(m.Id, m.UserId, u?.Email ?? string.Empty, u?.DisplayName ?? string.Empty, m.Role, m.Status == MembershipStatus.Active, m.CreatedAtUtc);
        });
        return new PagedResult<MemberDto>(items, page, pageSize, total);
    }

    public Task ChangeRoleAsync(Guid membershipId, TenantRole role, CancellationToken ct)
    {
        if (!role.IsStaff())
        {
            throw AppException.BadRequest("Only staff roles can be assigned.");
        }

        return UpdateAsync(membershipId, m => m.ChangeRole(role, clock.GetUtcNow().UtcDateTime), ct);
    }

    public Task SetActiveAsync(Guid membershipId, bool isActive, CancellationToken ct) =>
        UpdateAsync(membershipId, m => m.SetActive(isActive, clock.GetUtcNow().UtcDateTime), ct);

    /// <summary>
    /// Applies a change and keeps at least one USABLE Owner (active membership AND active user). EVERY membership mutation
    /// rotates the tenant OwnerChangeStamp, which was read BEFORE the actor/owner checks, so any concurrent change to the
    /// actor's or an Owner's authorization (demotion, suspension, deactivation) makes this commit fail; the retry then
    /// re-reads the actor's CURRENT authorization and the Owner count against fresh data. Rejected attempts roll back fully.
    /// </summary>
    private async Task UpdateAsync(Guid membershipId, Action<Membership> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // UPDATED: stamp pehle, phir actor ki current (membership + user status) authorization; stamp har mutation par rotate hota hai
            var tenant = await db.Tenants.SingleAsync(ct);
            await actor.RequireOwnerAsync(ct);

            var member = await db.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId, ct)
                ?? throw AppException.NotFound("Member not found.");
            if (!member.Role.IsStaff())
            {
                throw AppException.BadRequest("Only staff memberships can be managed here.");
            }

            var removesUsableOwner = member.IsActiveOwner && await db.Users.AnyAsync(u => u.Id == member.UserId && u.Status == UserStatus.Active, ct);
            change(member);

            if (removesUsableOwner && !member.IsActiveOwner)
            {
                var otherOwners = await db.UsableOwners().CountAsync(m => m.Id != member.Id, ct);
                if (otherOwners == 0)
                {
                    throw AppException.Conflict("A tenant must keep at least one active Owner.");
                }
            }

            tenant.RotateOwnerChangeStamp();

            try
            {
                await db.SaveChangesAsync(ct);
                LogChanged(logger, member.TenantId, member.Id, member.Role, member.Status);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        throw AppException.Conflict("The membership was changed concurrently.", "Please retry.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Membership {MembershipId} in tenant {TenantId} is now {Role}/{Status}")]
    private static partial void LogChanged(ILogger logger, Guid tenantId, Guid membershipId, TenantRole role, MembershipStatus status);
}
