using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record CreateOrganizationRequest(
    [Required, StringLength(100, MinimumLength = 2)] string Name
);
