using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    public async Task<ActionResult<TenantSettingsDto>> GetAsync(CancellationToken ct)
    {
        var role = await actor.GetActiveRoleAsync(ct);
        if (role is null)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (role == TenantRole.Customer)
        {
            return ActorAccess.Forbidden("Customers cannot view organization settings.");
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
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Organization not found." });
        }

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
    public async Task<ActionResult<TenantSettingsDto>> UpdateAsync(string reportingTimeZone, string? name, CancellationToken ct)
    {
        if (!ReportingTimeZoneId.TryValidate(reportingTimeZone, out var zone, out var zoneError))
        {
            return FieldError("ReportingTimeZone", zoneError);
        }

        string? newName = null;
        if (name is not null)
        {
            if (!DisplayNameRules.TryValidate(name, out var validName, out var nameError))
            {
                return FieldError("Name", nameError);
            }

            newName = validName;
        }

        if (await actor.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, role) = begun;
        await using var _ = transaction;
        if (role != TenantRole.TenantOwner)
        {
            return ActorAccess.Forbidden("Only an Owner can change organization settings.");
        }

        var settings = await db.Settings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "Organization settings are not initialized.",
                    Detail = "An Owner must initialize the tenant defaults.",
                }
            );
        }

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

    private static BadRequestObjectResult FieldError(string field, string message) =>
        new(new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = [message] }));

    [LoggerMessage(Level = LogLevel.Information, Message = "Reporting timezone of tenant {TenantId} changed to {TimeZoneId}")]
    private static partial void LogChanged(ILogger logger, Guid tenantId, string timeZoneId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization {TenantId} renamed")]
    private static partial void LogRenamed(ILogger logger, Guid tenantId);
}
