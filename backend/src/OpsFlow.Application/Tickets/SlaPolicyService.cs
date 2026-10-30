using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public sealed record SlaPolicyDto(Guid Id, TicketPriority Priority, int ResponseMinutes, int ResolutionMinutes, bool IsActive);

/// <summary>Tenant SLA policies (calendar-time minutes). Staff can read them; Owners/Managers manage them. Existing cycles keep their snapshot.</summary>
public sealed class SlaPolicyService(IOpsFlowDbContext db, ActorAccess actor, TimeProvider clock)
{
    public async Task<IReadOnlyList<SlaPolicyDto>> ListAsync(CancellationToken ct)
    {
        if (await actor.RequireActiveRoleAsync(ct) == TenantRole.Customer)
        {
            throw AppException.Forbidden("Customers cannot view SLA policies.");
        }

        return await db.SlaPolicies.AsNoTracking().OrderBy(p => p.Priority == TicketPriority.Urgent ? 4 : p.Priority == TicketPriority.High ? 3 : p.Priority == TicketPriority.Medium ? 2 : 1)
            .ThenBy(p => p.Id).Select(p => new SlaPolicyDto(p.Id, p.Priority, p.ResponseMinutes, p.ResolutionMinutes, p.IsActive)).ToListAsync(ct);
    }

    public async Task<SlaPolicyDto> CreateAsync(TicketPriority priority, int responseMinutes, int resolutionMinutes, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage SLA policies.", ct); // UPDATED: authorization commit tak valid
        var policy = Build(() => SlaPolicy.Create(actor.TenantId, priority, responseMinutes, resolutionMinutes, clock.GetUtcNow().UtcDateTime));
        db.SlaPolicies.Add(policy);
        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDto(policy);
    }

    public async Task<SlaPolicyDto> UpdateAsync(Guid id, int responseMinutes, int resolutionMinutes, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage SLA policies.", ct);
        var policy = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw NotFound();
        Build(() =>
        {
            policy.UpdateTargets(responseMinutes, resolutionMinutes, clock.GetUtcNow().UtcDateTime);
            return policy;
        });
        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDto(policy);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage SLA policies.", ct);
        var policy = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw NotFound();
        if (!isActive && policy.IsActive)
        {
            // UPDATED: har priority ke liye ek active policy zaroori hai (ek hi active ho sakti hai): deactivate karne ke bajaye targets edit karein
            throw AppException.Conflict("A priority must keep an active SLA policy.", "Edit the policy targets instead of deactivating it.");
        }

        policy.SetActive(isActive, clock.GetUtcNow().UtcDateTime);
        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static T Build<T>(Func<T> factory)
    {
        try
        {
            return factory();
        }
        catch (ArgumentException ex)
        {
            throw AppException.BadRequest("Invalid SLA targets.", ex.Message);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.SlaPolicyPriority))
        {
            throw AppException.Conflict("An active SLA policy already exists for this priority.");
        }
    }

    private static AppException NotFound() => AppException.NotFound("SLA policy not found.");

    private static SlaPolicyDto ToDto(SlaPolicy p) => new(p.Id, p.Priority, p.ResponseMinutes, p.ResolutionMinutes, p.IsActive);
}
