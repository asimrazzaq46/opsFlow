namespace OpsFlow.Application.Tenants;

/// <summary>
/// Effective organization settings. <c>ReportingTimeZone</c> is the authoritative IANA id used for day-based analytics. <c>Slug</c> and
/// <c>TicketNumberPrefix</c> are read-only: changing them would rewrite existing identifiers and ticket numbers.
/// </summary>
public sealed record TenantSettingsDto(
    string ReportingTimeZone,
    string Name,
    string Slug,
    string TicketNumberPrefix
);
