using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Analytics;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tenants;

/// <summary>
/// Reads and changes organization settings of the CURRENT tenant (resolved from the route and membership, never from the body).
/// Reading is staff-only; changing is Owner-only, re-checked from the database inside the write transaction. Nothing here is cached:
/// the tenant cache holds categories and SLA policies only, and the reporting timezone is read per request.
/// </summary>
public sealed partial class TenantSettingsService(
    IOpsFlowDbContext db,
    ActorAccess actor,
    ITenantContext tenantContext,
    TimeProvider clock,
    ILogger<TenantSettingsService> logger
)
{
    public async Task<TenantSettingsDto> GetAsync(CancellationToken ct)
    {
        if (await actor.RequireActiveRoleAsync(ct) == TenantRole.Customer)
        {
            throw AppException.Forbidden("Customers cannot view organization settings.");
        }

        // One round trip; legacy tenants without a settings row report the defaults.
        var row = await db
            .Tenants.AsNoTracking()
            .Select(t => new
            {
                t.Name,
                t.Slug,
                Zone = db.Settings.Select(s => s.ReportingTimeZone).FirstOrDefault(),
                Prefix = db.Settings.Select(s => s.TicketNumberPrefix).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Organization not found.");
        return new TenantSettingsDto(
            row.Zone ?? TenantSettings.DefaultReportingTimeZone,
            row.Name,
            row.Slug,
            row.Prefix ?? TenantSettings.DefaultTicketPrefix
        );
    }

    /// <summary>
    /// Updates the reporting timezone and, when given, the organization name. Unchanged values are not written. The tenant row carries
    /// the OwnerChangeStamp concurrency token, so a rename racing an ownership change fails with 409 instead of committing on stale
    /// authorization. Categories, SLA policies, memberships and existing SLA deadlines are never touched.
    /// </summary>
    public async Task<TenantSettingsDto> UpdateAsync(string reportingTimeZone, string? name, CancellationToken ct)
    {
        var zone = ReportingTimeZoneId.Validate(reportingTimeZone, "ReportingTimeZone");
        var newName = name is null ? null : DisplayNameRules.Validate(name, "Name");
        var (transaction, role) = await actor.BeginAuthorizedAsync(null, ct);
        await using var _ = transaction;
        if (role != TenantRole.TenantOwner)
        {
            throw AppException.Forbidden("Only an Owner can change organization settings.");
        }

        var settings = await db.Settings.SingleOrDefaultAsync(ct)
            ?? throw AppException.Conflict("Organization settings are not initialized.", "An Owner must initialize the tenant defaults.");
        var tenant = await db.Tenants.SingleAsync(ct);
        var tenantId = tenantContext.TenantId!.Value;
        if (settings.ReportingTimeZone != zone)
        {
            settings.SetReportingTimeZone(zone, clock.GetUtcNow().UtcDateTime);
            LogChanged(logger, tenantId, zone);
        }

        if (newName is not null && tenant.Name != newName)
        {
            tenant.Rename(newName);
            LogRenamed(logger, tenantId);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new TenantSettingsDto(settings.ReportingTimeZone, tenant.Name, tenant.Slug, settings.TicketNumberPrefix);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reporting timezone of tenant {TenantId} changed to {TimeZoneId}")]
    private static partial void LogChanged(ILogger logger, Guid tenantId, string timeZoneId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization {TenantId} renamed")]
    private static partial void LogRenamed(ILogger logger, Guid tenantId);
}
