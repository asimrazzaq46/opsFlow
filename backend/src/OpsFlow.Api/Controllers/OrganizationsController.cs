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
public sealed class OrganizationsController(OrganizationService organizations, ICurrentUser user) : ControllerBase
{
    [HttpGet(Name = "ListMyOrganizations")]
    [EndpointSummary("List user organizations")]
    [EndpointDescription("Returns organizations where the authenticated user has an active membership.")]
    public async Task<ActionResult<IReadOnlyList<OrganizationDto>>> ListMyOrganizations(CancellationToken ct)
    {
        return Ok(await organizations.ListMineAsync(user.UserId, ct));
    }

    [HttpPost(Name = "CreateOrganization")]
    [EndpointSummary("Create organization")]
    [EndpointDescription("Creates a tenant with the caller as its first Owner and seeds the default ticket settings, categories and SLA policies.")]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<OrganizationDto>> CreateOrganization(CreateOrganizationRequest request, CancellationToken ct)
    {
        var org = await organizations.CreateAsync(user.UserId, request.Name, ct);
        return Created($"/api/v1/tenants/{org.Id}", org);
    }
}
