using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public sealed record TicketCategoryDto(Guid Id, string Name, bool IsActive);

/// <summary>Everyone in the tenant can list the ACTIVE categories (needed to create tickets); Owners/Managers manage them and see inactive ones.</summary>
public sealed class TicketCategoryService(IOpsFlowDbContext db, ActorAccess actor, ITenantCache cache, OutboxWriter outbox, TimeProvider clock)
{
    public async Task<IReadOnlyList<TicketCategoryDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var role = await actor.RequireActiveRoleAsync(ct);
        var all = includeInactive && role is TenantRole.TenantOwner or TenantRole.Manager;
        // UPDATED: role check upar ho chuka; cache mein tenant ki poori (inactive samet) list hai, filter role ke mutabiq yahan hota hai
        var categories = await cache.GetOrCreateAsync<List<TicketCategoryDto>>(actor.TenantId, CacheResources.Categories,
            c => db.TicketCategories.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new TicketCategoryDto(x.Id, x.Name, x.IsActive)).ToListAsync(c), ct);
        return categories.Where(c => all || c.IsActive).ToList();
    }

    public async Task<TicketCategoryDto> CreateAsync(string name, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage categories.", ct); // UPDATED: authorization commit tak valid
        var normalized = TicketCategory.Normalize(name);
        if (await db.TicketCategories.AnyAsync(c => c.NormalizedName == normalized, ct))
        {
            throw AppException.Conflict("A category with this name already exists.");
        }

        var category = TicketCategory.Create(actor.TenantId, name, clock.GetUtcNow().UtcDateTime);
        db.TicketCategories.Add(category);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(actor.TenantId, CacheResources.Categories, CancellationToken.None);
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
    }

    public async Task<TicketCategoryDto> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage categories.", ct);
        var normalized = TicketCategory.Normalize(name);
        if (await db.TicketCategories.AnyAsync(c => c.Id != id && c.NormalizedName == normalized, ct))
        {
            throw AppException.Conflict("A category with this name already exists.");
        }

        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        category.Rename(name, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        await SaveAsync(ct);
        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(actor.TenantId, CacheResources.Categories, CancellationToken.None);
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        await using var transaction = await actor.BeginManagerAsync("Only Owners and Managers can manage categories.", ct);
        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        category.SetActive(isActive, clock.GetUtcNow().UtcDateTime);
        outbox.StageCacheInvalidation(actor.TenantId, CacheResources.Categories);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await cache.InvalidateAsync(actor.TenantId, CacheResources.Categories, CancellationToken.None);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.CategoryName))
        {
            throw AppException.Conflict("A category with this name already exists.");
        }
    }

    private static AppException NotFound() => AppException.NotFound("Category not found.");
}
