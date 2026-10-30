using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tenants;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Organizations;

public sealed record OrganizationDto(Guid Id, string Name, string Slug, TenantRole Role);

public sealed partial class OrganizationService(IOpsFlowDbContext db, ITenantContext tenantContext, TimeProvider clock, ILogger<OrganizationService> logger)
{
    private const int MaxSlugLength = 50;

    /// <summary>Creates the tenant and its first Owner membership in one atomic SaveChanges.</summary>
    public async Task<OrganizationDto> CreateAsync(Guid userId, string name, CancellationToken ct)
    {
        name = name.Trim();
        var tenantId = Guid.NewGuid();
        var slug = await UniqueSlugAsync(name, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        using var scope = tenantContext.Begin(tenantId);
        db.Tenants.Add(Tenant.Create(tenantId, name, slug, now));
        db.Memberships.Add(Membership.Create(tenantId, userId, TenantRole.TenantOwner, now));
        TenantDefaults.AddAll(db, tenantId, now); // UPDATED: settings, ticket counter, default categories aur SLA policies usi atomic SaveChanges mein
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.TenantSlug))
        {
            // UPDATED: sirf slug race 409 hai; doosri DB failures ab 500 ke tor par propagate hoti hain
            throw AppException.Conflict("The organization could not be created.", "Please try again.");
        }

        LogCreated(logger, tenantId, userId);
        return new OrganizationDto(tenantId, name, slug, TenantRole.TenantOwner);
    }

    public async Task<IReadOnlyList<OrganizationDto>> ListMineAsync(Guid userId, CancellationToken ct) =>
        await (from m in db.Memberships.IgnoreQueryFilters()
               join t in db.Tenants.IgnoreQueryFilters() on m.TenantId equals t.Id
               where m.UserId == userId && m.Status == MembershipStatus.Active
               orderby t.Name
               select new OrganizationDto(t.Id, t.Name, t.Slug, m.Role)).ToListAsync(ct);

    public async Task<OrganizationDto> GetCurrentAsync(CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Organization not found.");
        return new OrganizationDto(tenant.Id, tenant.Name, tenant.Slug, tenantContext.Role!.Value);
    }

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var slug = NonSlugChars().Replace(name.ToLowerInvariant(), "-").Trim('-');
        slug = slug.Length == 0 ? "org" : slug[..Math.Min(slug.Length, MaxSlugLength)].TrimEnd('-');
        return await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct)
            ? $"{slug}-{Guid.NewGuid():N}"[..(slug.Length + 7)]
            : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization {TenantId} created by user {UserId}")]
    private static partial void LogCreated(ILogger logger, Guid tenantId, Guid userId);
}
