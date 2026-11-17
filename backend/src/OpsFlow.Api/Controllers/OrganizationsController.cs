using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Organizations;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/organizations")]
[Authorize]
public sealed class OrganizationsController(OrganizationService organizations, ICurrentUser user)
    : ControllerBase
{
    [HttpGet(Name = "ListMyOrganizations")]
    [EndpointSummary("List user organizations")]
    [EndpointDescription(
        "Returns organizations where the authenticated user has an active membership."
    )]
    public async Task<ActionResult<IReadOnlyList<OrganizationDto>>> ListMyOrganizations(
        CancellationToken ct
    )
    {
        return Ok(await organizations.ListMineAsync(user.UserId, ct));
    }

    [HttpPost(Name = "CreateOrganization")]
    [EndpointSummary("Create organization")]
    [EndpointDescription(
        "Creates a tenant with the caller as its first Owner and seeds the default ticket settings, categories and SLA policies. The optional slug (lowercase letters, digits, hyphens; 2-50) and ticket number prefix (uppercase letters and digits, starting with a letter; 2-10) are chosen here once and can never be changed; omitted, they are derived from the name and the default. 400 for invalid values, 409 SLUG_TAKEN when the slug is in use."
    )]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<OrganizationDto>> CreateOrganization(
        CreateOrganizationRequest request,
        CancellationToken ct
    )
    {
        var result = await organizations.CreateAsync(user.UserId, request.Name, request.Slug, request.TicketNumberPrefix, ct);
        return result.Result is ObjectResult failure
            ? this.Answer(failure)
            : Created($"/api/v1/tenants/{result.Value!.Id}", result.Value);
    }
}
