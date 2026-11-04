using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>Tenant SLA policies (calendar-time minutes). Staff can read them; Owners/Managers manage them. Existing cycles keep their snapshot.</summary>
public sealed class SlaPolicyService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    ITenantCache cache,
    OutboxWriter outbox,
    TimeProvider clock
)
{
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> ListAsync(CancellationToken ct)
    {
        var role = await actor.GetActiveRoleAsync(ct);
        if (role is null)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (role == TenantRole.Customer)
        {
            return ActorAccess.Forbidden("Customers cannot view SLA policies.");
        }

        // Staff-only data, role already checked above; tenant-wide cache-aside.
        return await cache.GetOrCreateAsync<List<SlaPolicyDto>>(
            actor.TenantId,
            CacheResources.SlaPolicies,
            c =>
                db.SlaPolicies.AsNoTracking()
                    .OrderBy(p =>
                        p.Priority == TicketPriority.Urgent ? 4
                        : p.Priority == TicketPriority.High ? 3
                        : p.Priority == TicketPriority.Medium ? 2
                        : 1
                    )
                    .ThenBy(p => p.Id)
                    .Select(p => new SlaPolicyDto(
                        p.Id,
                        p.Priority,
                        p.ResponseMinutes,
                        p.ResolutionMinutes,
                        p.IsActive
                    ))
                    .ToListAsync(c),
            ct
        );
    }

    public async Task<ActionResult<SlaPolicyDto>> CreateAsync(
        TicketPriority priority,
        int responseMinutes,
        int resolutionMinutes,
        CancellationToken ct
    )
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage SLA policies.");
        }

        if (!SlaPolicy.AreTargetsValid(responseMinutes, resolutionMinutes))
        {
            return InvalidTargets();
        }

        var policy = SlaPolicy.Create(
            actor.TenantId,
            priority,
            responseMinutes,
            resolutionMinutes,
            clock.GetUtcNow().UtcDateTime
        );
        db.SlaPolicies.Add(policy);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.SlaPolicies);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.SlaPolicies,
            CancellationToken.None
        );
        return ToDto(policy);
    }

    public async Task<ActionResult<SlaPolicyDto>> UpdateAsync(
        Guid id,
        int responseMinutes,
        int resolutionMinutes,
        CancellationToken ct
    )
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage SLA policies.");
        }

        var policy = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return PolicyNotFound();
        }

        if (!SlaPolicy.AreTargetsValid(responseMinutes, resolutionMinutes))
        {
            return InvalidTargets();
        }

        policy.UpdateTargets(responseMinutes, resolutionMinutes, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.SlaPolicies);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.SlaPolicies,
            CancellationToken.None
        );
        return ToDto(policy);
    }

    /// <returns>Null when applied; otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage SLA policies.");
        }

        var policy = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return PolicyNotFound();
        }

        if (!isActive && policy.IsActive)
        {
            // Every priority needs exactly one active policy, so edit its targets instead of deactivating it.
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "A priority must keep an active SLA policy.",
                    Detail = "Edit the policy targets instead of deactivating it.",
                }
            );
        }

        policy.SetActive(isActive, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.SlaPolicies);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.SlaPolicies,
            CancellationToken.None
        );
        return null;
    }

    private static BadRequestObjectResult InvalidTargets() =>
        new(
            new ProblemDetails
            {
                Title = "Invalid SLA targets.",
                Detail = SlaPolicy.TargetsRequirement,
            }
        );

    /// <returns>Null when saved; the 409 when the priority already has an active policy.</returns>
    private async Task<ObjectResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.SlaPolicyPriority))
        {
            return new ConflictObjectResult(
                new ProblemDetails { Title = "An active SLA policy already exists for this priority." }
            );
        }
    }

    private static NotFoundObjectResult PolicyNotFound() =>
        new(new ProblemDetails { Title = "SLA policy not found." });

    private static SlaPolicyDto ToDto(SlaPolicy p) =>
        new(p.Id, p.Priority, p.ResponseMinutes, p.ResolutionMinutes, p.IsActive);
}
