using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Tenants;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure.Initialization;

/// <summary>
/// Development only: initializes the ticket defaults (settings, counter, categories, SLA policies) of the already existing demo
/// organization, which was created before they existed. Idempotent; it never touches any other tenant.
/// </summary>
public sealed partial class DevelopmentTenantDefaultsSeeder(
    OpsFlowDbContext db,
    TenantDefaultsService defaults,
    ITenantContext tenantContext,
    IOptions<DevelopmentSeedOptions> options,
    ILogger<DevelopmentTenantDefaultsSeeder> logger) : IDevelopmentSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var name = options.Value.OrganizationName;
        var tenantId = await db.Tenants.IgnoreQueryFilters().Where(t => t.Name == name).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null)
        {
            return;
        }

        using var scope = tenantContext.Begin(tenantId.Value);
        var added = await defaults.EnsureAsync(cancellationToken);
        LogEnsured(logger, name, added);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Development tenant '{Name}' ticket defaults ensured ({Added} records added)")]
    private static partial void LogEnsured(ILogger logger, string name, int added);
}
