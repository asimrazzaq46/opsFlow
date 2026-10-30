using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Organizations;
using OpsFlow.Domain.Common;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

public interface IDevelopmentSeeder
{
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Seeds the demo organization and its first TenantOwner through the normal account + provisioning paths (password policy
/// and hashing included). Idempotent: it only acts when neither the user nor the organization exists, so an existing
/// account (seeded or unrelated) is never modified, re-passworded or granted access.
/// </summary>
public sealed partial class DevelopmentAdminSeeder(
    AccountService accounts,
    OrganizationService organizations,
    OpsFlowDbContext db,
    IOptions<DevelopmentSeedOptions> options,
    ILogger<DevelopmentAdminSeeder> logger
) : IDevelopmentSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            LogPasswordMissing(logger);
            return;
        }

        // UPDATED: Identity UserManager ki jagah domain User table
        var normalized = EmailAddress.Normalize(seed.AdminEmail);
        var existing = await db
            .Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (existing is not null)
        {
            var hasMembership = await db
                .Memberships.IgnoreQueryFilters()
                .AnyAsync(m => m.UserId == existing.Id, cancellationToken);
            LogUserExists(logger, hasMembership);
            return;
        }

        if (
            await db
                .Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Name == seed.OrganizationName, cancellationToken)
        )
        {
            LogOrganizationExists(logger, seed.OrganizationName);
            return;
        }

        // User and organization are created in ONE transaction: a failure leaves nothing half-seeded.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user =
            await accounts.CreateAsync(
                seed.AdminEmail,
                seed.AdminDisplayName,
                seed.AdminPassword,
                emailVerified: true,
                cancellationToken
            )
            ?? throw new InvalidOperationException(
                "Seeding the development admin failed: the email is already registered."
            );

        var organization = await organizations.CreateAsync(
            user.Id,
            seed.OrganizationName,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        LogSeeded(logger, organization.Id, seed.OrganizationName);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Development admin NOT seeded: DevelopmentSeed:AdminPassword is not configured. Run: dotnet user-secrets set \"DevelopmentSeed:AdminPassword\" \"<strong password>\" --project backend/src/OpsFlow.Api"
    )]
    private static partial void LogPasswordMissing(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development admin already exists (has memberships: {HasMembership}); nothing changed and no access was granted."
    )]
    private static partial void LogUserExists(ILogger logger, bool hasMembership);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Organization '{Name}' already exists without the seed admin; seeding skipped to avoid duplicates."
    )]
    private static partial void LogOrganizationExists(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Seeded development organization {TenantId} ('{Name}') with its TenantOwner."
    )]
    private static partial void LogSeeded(ILogger logger, Guid tenantId, string name);
}
