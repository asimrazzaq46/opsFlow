using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

/// <summary>
/// <c>Slug</c> and <c>TicketNumberPrefix</c> are optional: omitted, the slug is derived from the name and the prefix is the default.
/// Both are fixed for the life of the organization.
/// </summary>
public sealed record CreateOrganizationRequest(
    [Required, StringLength(100, MinimumLength = 2)] string Name,
    [StringLength(50, MinimumLength = 2)] string? Slug = null,
    [StringLength(10, MinimumLength = 2)] string? TicketNumberPrefix = null
);
