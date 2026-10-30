using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public sealed record TicketCategoryDto(Guid Id, string Name, bool IsActive);

/// <summary>Everyone in the tenant can list the ACTIVE categories (needed to create tickets); Owners/Managers manage them and see inactive ones.</summary>
public sealed class TicketCategoryService(IOpsFlowDbContext db, ActorAccess actor, TimeProvider clock)
{
    public async Task<IReadOnlyList<TicketCategoryDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var role = await actor.RequireActiveRoleAsync(ct);
        var all = includeInactive && role is TenantRole.TenantOwner or TenantRole.Manager;
        return await db.TicketCategories.AsNoTracking().Where(c => all || c.IsActive)
            .OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new TicketCategoryDto(c.Id, c.Name, c.IsActive)).ToListAsync(ct);
    }

    public async Task<TicketCategoryDto> CreateAsync(string name, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var normalized = TicketCategory.Normalize(name);
        if (await db.TicketCategories.AnyAsync(c => c.NormalizedName == normalized, ct))
        {
            throw AppException.Conflict("A category with this name already exists.");
        }

        var category = TicketCategory.Create(actor.TenantId, name, clock.GetUtcNow().UtcDateTime);
        db.TicketCategories.Add(category);
        await SaveAsync(ct);
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
    }

    public async Task<TicketCategoryDto> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var normalized = TicketCategory.Normalize(name);
        if (await db.TicketCategories.AnyAsync(c => c.Id != id && c.NormalizedName == normalized, ct))
        {
            throw AppException.Conflict("A category with this name already exists.");
        }

        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        category.Rename(name, clock.GetUtcNow().UtcDateTime);
        await SaveAsync(ct);
        return new TicketCategoryDto(category.Id, category.Name, category.IsActive);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        await RequireManagerAsync(ct);
        var category = await db.TicketCategories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
        category.SetActive(isActive, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
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

    private async Task RequireManagerAsync(CancellationToken ct)
    {
        if (await actor.RequireActiveRoleAsync(ct) is not (TenantRole.TenantOwner or TenantRole.Manager))
        {
            throw AppException.Forbidden("Only Owners and Managers can manage categories.");
        }
    }

    private static AppException NotFound() => AppException.NotFound("Category not found.");
}
