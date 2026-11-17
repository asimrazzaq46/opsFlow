using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tenants;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Organizations;

public sealed partial class OrganizationService(
    IOpsFlowDbContext db,
    ITenantContext tenantContext,
    TimeProvider clock,
    ILogger<OrganizationService> logger
)
{
    private const int MaxSlugLength = 50;

    /// <summary>Creates the tenant with a derived slug and the default ticket prefix; null when a concurrent creation took the slug.</summary>
    public async Task<OrganizationDto?> CreateAsync(Guid userId, string name, CancellationToken ct) =>
        (await CreateAsync(userId, name, null, null, ct)).Value;

    /// <summary>
    /// Creates the tenant, its first Owner membership and its settings (including the chosen ticket prefix) in one atomic SaveChanges.
    /// An explicit slug is validated and must be unused (409 SLUG_TAKEN, also when a concurrent creation wins the unique index);
    /// omitted values fall back to the derived slug and the default prefix.
    /// </summary>
    public async Task<ActionResult<OrganizationDto>> CreateAsync(
        Guid userId,
        string name,
        string? requestedSlug,
        string? requestedPrefix,
        CancellationToken ct
    )
    {
        var errors = new Dictionary<string, string[]>();
        var slug = string.Empty;
        if (requestedSlug is not null && !WorkspaceIdentifierRules.TryNormalizeSlug(requestedSlug, out slug, out var slugError))
        {
            errors["Slug"] = [slugError];
        }

        string? prefix = null;
        if (requestedPrefix is not null)
        {
            if (WorkspaceIdentifierRules.TryNormalizePrefix(requestedPrefix, out var validPrefix, out var prefixError))
            {
                prefix = validPrefix;
            }
            else
            {
                errors["TicketNumberPrefix"] = [prefixError];
            }
        }

        if (errors.Count > 0)
        {
            return new BadRequestObjectResult(new ValidationProblemDetails(errors));
        }

        name = name.Trim();
        var tenantId = Guid.NewGuid();
        var explicitSlug = requestedSlug is not null;
        if (explicitSlug)
        {
            if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct))
            {
                return SlugTaken();
            }
        }
        else
        {
            slug = await UniqueSlugAsync(name, ct);
        }

        var now = clock.GetUtcNow().UtcDateTime;

        using var scope = tenantContext.Begin(tenantId);
        db.Tenants.Add(Tenant.Create(tenantId, name, slug, now));
        var membership = Membership.Create(tenantId, userId, TenantRole.TenantOwner, now);
        db.Memberships.Add(membership);
        TenantDefaults.AddAll(db, tenantId, now, prefix); // settings, ticket counter, default categories and SLA policies in the same atomic SaveChanges
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.TenantSlug))
        {
            // Only the slug race is an expected outcome; other DB failures propagate as a 500.
            return explicitSlug
                ? SlugTaken()
                : new ConflictObjectResult(
                    new ProblemDetails { Title = "The organization could not be created.", Detail = "Please try again." }
                );
        }

        LogCreated(logger, tenantId, userId);
        return new OrganizationDto(tenantId, name, slug, TenantRole.TenantOwner, membership.Id);
    }

    public async Task<IReadOnlyList<OrganizationDto>> ListMineAsync(
        Guid userId,
        CancellationToken ct
    ) =>
        await (
            from m in db.Memberships.IgnoreQueryFilters()
            join t in db.Tenants.IgnoreQueryFilters() on m.TenantId equals t.Id
            where m.UserId == userId && m.Status == MembershipStatus.Active
            orderby t.Name
            select new OrganizationDto(t.Id, t.Name, t.Slug, m.Role, m.Id)
        ).ToListAsync(ct);

    /// <returns>Null when the tenant does not exist.</returns>
    public async Task<OrganizationDto?> GetCurrentAsync(CancellationToken ct)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(ct);
        return tenant is null
            ? null
            : new OrganizationDto(
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenantContext.Role!.Value,
                tenantContext.MembershipId!.Value
            );
    }

    private static ConflictObjectResult SlugTaken() =>
        new(
            new ProblemDetails
            {
                Title = "This workspace slug is already in use.",
                Detail = "Choose a different slug.",
                Extensions = { ["code"] = "SLUG_TAKEN" },
            }
        );

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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Organization {TenantId} created by user {UserId}"
    )]
    private static partial void LogCreated(ILogger logger, Guid tenantId, Guid userId);
}
