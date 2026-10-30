namespace OpsFlow.Application.Tenants;

/// <summary>Tenant-wide settings visible to staff. <c>ReportingTimeZone</c> is the authoritative IANA id used for day-based analytics.</summary>
public sealed record TenantSettingsDto(string ReportingTimeZone);
