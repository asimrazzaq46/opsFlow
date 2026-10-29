using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Memberships;

public sealed record MemberDto(Guid Id, Guid UserId, string Email, string DisplayName, TenantRole Role, bool IsActive, DateTime CreatedAtUtc);

public sealed partial class MembershipService(IOpsFlowDbContext db, ITenantContext tenantContext, IUserDirectory users, TimeProvider clock, ILogger<MembershipService> logger)
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
    /// Applies a change and keeps at least one active Owner. Any change that removes an Owner rotates the tenant
    /// OwnerChangeStamp, so a concurrent removal fails the concurrency check and re-evaluates against fresh data.
    /// </summary>
    private async Task UpdateAsync(Guid membershipId, Action<Membership> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // UPDATED: har attempt par (retry par bhi) stamp PEHLE read hota hai, phir actor ki Owner authorization dobara verify hoti hai
            var tenant = await db.Tenants.SingleAsync(ct);
            await EnsureActorIsActiveOwnerAsync(ct);

            var member = await db.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId, ct)
                ?? throw AppException.NotFound("Member not found.");
            if (!member.Role.IsStaff())
            {
                throw AppException.BadRequest("Only staff memberships can be managed here.");
            }

            var wasActiveOwner = member.IsActiveOwner;
            change(member);

            if (wasActiveOwner && !member.IsActiveOwner)
            {
                var otherOwners = await db.Memberships.CountAsync(m => m.Id != member.Id && m.Role == TenantRole.TenantOwner && m.Status == MembershipStatus.Active, ct);
                if (otherOwners == 0)
                {
                    throw AppException.Conflict("A tenant must keep at least one active Owner.");
                }

                tenant.RotateOwnerChangeStamp();
            }

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

    private async Task EnsureActorIsActiveOwnerAsync(CancellationToken ct)
    {
        var actorId = tenantContext.MembershipId;
        if (actorId is null || !await db.Memberships.AnyAsync(m => m.Id == actorId && m.Role == TenantRole.TenantOwner && m.Status == MembershipStatus.Active, ct))
        {
            throw AppException.Forbidden("Only an active Owner can change memberships.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Membership {MembershipId} in tenant {TenantId} is now {Role}/{Status}")]
    private static partial void LogChanged(ILogger logger, Guid tenantId, Guid membershipId, TenantRole role, MembershipStatus status);
}
