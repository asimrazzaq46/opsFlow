using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Organizations;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

public interface IDevelopmentSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Seeds the demo organization and its first TenantOwner through the normal Identity + provisioning paths.
/// Idempotent: it only acts when neither the user nor the organization exists, so an existing account (seeded or
/// unrelated) is never modified, re-passworded or granted access.
/// </summary>
public sealed partial class DevelopmentAdminSeeder(
    UserManager<ApplicationUser> users,
    OrganizationService organizations,
    OpsFlowDbContext db,
    IOptions<DevelopmentSeedOptions> options,
    TimeProvider clock,
    ILogger<DevelopmentAdminSeeder> logger) : IDevelopmentSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            LogPasswordMissing(logger);
            return;
        }

        var existing = await users.FindByEmailAsync(seed.AdminEmail);
        if (existing is not null)
        {
            var hasMembership = await db.Memberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == existing.Id, cancellationToken);
            LogUserExists(logger, hasMembership);
            return;
        }

        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Name == seed.OrganizationName, cancellationToken))
        {
            LogOrganizationExists(logger, seed.OrganizationName);
            return;
        }

        // User and organization are created in ONE transaction: a failure leaves nothing half-seeded.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = new ApplicationUser
        {
            UserName = seed.AdminEmail,
            Email = seed.AdminEmail,
            EmailConfirmed = true, // development convenience; sign-in requires a confirmed email
            DisplayName = seed.AdminDisplayName,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };

        var result = await users.CreateAsync(user, seed.AdminPassword); // full Identity password validation applies
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seeding the development admin failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
        }

        var organization = await organizations.CreateAsync(user.Id, seed.OrganizationName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogSeeded(logger, organization.Id, seed.OrganizationName);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Development admin NOT seeded: DevelopmentSeed:AdminPassword is not configured. Run: dotnet user-secrets set \"DevelopmentSeed:AdminPassword\" \"<strong password>\" --project backend/src/OpsFlow.Api")]
    private static partial void LogPasswordMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Development admin already exists (has memberships: {HasMembership}); nothing changed and no access was granted.")]
    private static partial void LogUserExists(ILogger logger, bool hasMembership);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Organization '{Name}' already exists without the seed admin; seeding skipped to avoid duplicates.")]
    private static partial void LogOrganizationExists(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development organization {TenantId} ('{Name}') with its TenantOwner.")]
    private static partial void LogSeeded(ILogger logger, Guid tenantId, string name);
}
