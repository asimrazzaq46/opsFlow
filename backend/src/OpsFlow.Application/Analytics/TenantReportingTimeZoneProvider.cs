using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Analytics;

/// <summary>
/// Reads the reporting timezone of the CURRENT tenant (the query filter scopes <c>Settings</c> to the membership-validated tenant, never to a
/// client value) once per request scope. A missing row (legacy tenant) or an unusable stored id falls back to UTC; the applied id is always
/// echoed in the dashboard/report <c>timeZone</c> field.
/// </summary>
public sealed partial class TenantReportingTimeZoneProvider(IOpsFlowDbContext db, ILogger<TenantReportingTimeZoneProvider> logger) : IReportingTimeZoneProvider
{
    private TimeZoneInfo? _cached;

    public async Task<TimeZoneInfo> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var id = await db.Settings.AsNoTracking().Select(s => s.ReportingTimeZone).SingleOrDefaultAsync(ct);
        return _cached = Resolve(id);
    }

    private TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == TenantSettings.DefaultReportingTimeZone)
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return ReportingCalendar.Resolve(id);
        }
        catch (ArgumentException)
        {
            LogInvalid(logger, id);
            return TimeZoneInfo.Utc;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored reporting timezone '{TimeZoneId}' is not available on this host; falling back to UTC")]
    private static partial void LogInvalid(ILogger logger, string timeZoneId);
}

/// <summary>Accepted shape of a reporting timezone id: exact IANA (Area/Location) or UTC; Windows ids are rejected so the value is portable across hosts.</summary>
public static partial class ReportingTimeZoneId
{
    [GeneratedRegex(@"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>Returns the id when it is a valid, exactly-spelled IANA id known to this host; otherwise throws a field validation error.</summary>
    public static string Validate(string? id, string field)
    {
        var value = id?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > TenantSettings.MaxReportingTimeZoneLength || !Shape().IsMatch(value))
        {
            throw AppException.Validation(field, "Use an IANA timezone id such as 'Europe/Rome' or 'UTC'.");
        }

        TimeZoneInfo zone;
        try
        {
            zone = ReportingCalendar.Resolve(value);
        }
        catch (ArgumentException)
        {
            throw AppException.Validation(field, $"'{value}' is not a known IANA timezone.");
        }

        return string.Equals(zone.Id, value, StringComparison.Ordinal) ? value : throw AppException.Validation(field, $"Use the exact IANA spelling '{zone.Id}'.");
    }
}
