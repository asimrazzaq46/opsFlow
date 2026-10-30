using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Analytics;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tenants;

/// <summary>Reads and changes tenant-wide settings. Reading is staff-only; changing the reporting timezone is Owner-only, re-checked from the database inside the write transaction.</summary>
public sealed partial class TenantSettingsService(IOpsFlowDbContext db, ActorAccess actor, ITenantContext tenantContext, TimeProvider clock, ILogger<TenantSettingsService> logger)
{
    public async Task<TenantSettingsDto> GetAsync(CancellationToken ct)
    {
        if (await actor.RequireActiveRoleAsync(ct) == TenantRole.Customer)
        {
            throw AppException.Forbidden("Customers cannot view organization settings.");
        }

        // legacy tenants without a settings row report the UTC fallback
        var zone = await db.Settings.AsNoTracking().Select(s => s.ReportingTimeZone).SingleOrDefaultAsync(ct);
        return new TenantSettingsDto(zone ?? TenantSettings.DefaultReportingTimeZone);
    }

    public async Task<TenantSettingsDto> UpdateReportingTimeZoneAsync(string reportingTimeZone, CancellationToken ct)
    {
        var zone = ReportingTimeZoneId.Validate(reportingTimeZone, "ReportingTimeZone");
        var (transaction, role) = await actor.BeginAuthorizedAsync(null, ct);
        await using var _ = transaction;
        if (role != TenantRole.TenantOwner)
        {
            throw AppException.Forbidden("Only an Owner can change organization settings.");
        }

        var settings = await db.Settings.SingleOrDefaultAsync(ct)
            ?? throw AppException.Conflict("Organization settings are not initialized.", "An Owner must initialize the tenant defaults.");
        if (settings.ReportingTimeZone != zone)
        {
            settings.SetReportingTimeZone(zone, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            LogChanged(logger, tenantContext.TenantId!.Value, zone);
        }

        await transaction.CommitAsync(ct);
        return new TenantSettingsDto(zone);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reporting timezone of tenant {TenantId} changed to {TimeZoneId}")]
    private static partial void LogChanged(ILogger logger, Guid tenantId, string timeZoneId);
}
