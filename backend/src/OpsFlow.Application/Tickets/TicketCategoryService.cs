using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>Everyone in the tenant can list the ACTIVE categories (needed to create tickets); Owners/Managers manage them and see inactive ones.</summary>
public sealed class TicketCategoryService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    ITenantCache cache,
    OutboxWriter outbox,
    TimeProvider clock
)
{
    /// <returns>Null when the caller's access is no longer valid.</returns>
    public async Task<IReadOnlyList<TicketCategoryDto>?> ListAsync(
        bool includeInactive,
        CancellationToken ct
    )
    {
        if (await actor.GetActiveRoleAsync(ct) is not { } role)
        {
            return null;
        }

        var all = includeInactive && role is TenantRole.TenantOwner or TenantRole.Manager;
        // Role already checked above; the cache holds the tenant's full list (incl. inactive), filtered by role here.
        var categories = await cache.GetOrCreateAsync<List<TicketCategoryDto>>(
            actor.TenantId,
            CacheResources.Categories,
            c =>
                db.TicketCategories.AsNoTracking()
                    .OrderBy(x => x.Name)
                    .ThenBy(x => x.Id)
                    .Select(x => new TicketCategoryDto(x.Id, x.Name, x.IsActive))
                    .ToListAsync(c),
            ct
        );
        return categories.Where(c => all || c.IsActive).ToList();
    }

    public async Task<ActionResult<TicketCategoryDto>> CreateAsync(string name, CancellationToken ct)
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage categories.");
        }
        var normalized = TicketCategory.Normalize(name);
        if (await db.TicketCategories.AnyAsync(c => c.NormalizedName == normalized, ct))
        {
            return DuplicateName();
        }

        var category = TicketCategory.Create(actor.TenantId, name, clock.GetUtcNow().UtcDateTime);
        db.TicketCategories.Add(category);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.Categories,
            CancellationToken.None
        );
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
    }

    public async Task<ActionResult<TicketCategoryDto>> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction; // every early return rolls back: nothing is committed
        if (role is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            return ActorAccess.Forbidden("Only Owners and Managers can manage categories.");
        }
        var normalized = TicketCategory.Normalize(name);
        if (
            await db.TicketCategories.AnyAsync(
                c => c.Id != id && c.NormalizedName == normalized,
                ct
            )
        )
        {
            return DuplicateName();
        }

        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return CategoryNotFound();
        }

        category.Rename(name, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        if (await SaveAsync(ct) is { } saveFailure)
        {
            return saveFailure;
        }

        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.Categories,
            CancellationToken.None
        );
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
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
            return ActorAccess.Forbidden("Only Owners and Managers can manage categories.");
        }
        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return CategoryNotFound();
        }

        category.SetActive(isActive, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(
            actor.TenantId,
            CacheResources.Categories,
            CancellationToken.None
        );
        return null;
    }

    /// <returns>Null when saved; the 409 when the name is taken.</returns>
    private async Task<ObjectResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.CategoryName))
        {
            return DuplicateName();
        }
    }

    private static ConflictObjectResult DuplicateName() =>
        new(new ProblemDetails { Title = "A category with this name already exists." });

    private static NotFoundObjectResult CategoryNotFound() =>
        new(new ProblemDetails { Title = "Category not found." });
}
