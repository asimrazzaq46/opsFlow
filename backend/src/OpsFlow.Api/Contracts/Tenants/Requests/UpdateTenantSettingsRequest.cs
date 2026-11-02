using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>
/// The browser timezone may be offered as a suggestion by the UI, but it only takes effect through this Owner-authorized call.
/// <c>Name</c> is optional: null keeps the current organization name.
/// </summary>
public sealed record UpdateTenantSettingsRequest(
    [Required, StringLength(64, MinimumLength = 1)] string ReportingTimeZone,
    [StringLength(100, MinimumLength = 2)] string? Name = null
);
