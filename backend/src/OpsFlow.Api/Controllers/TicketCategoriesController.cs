using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/ticket-categories")]
public sealed class TicketCategoriesController(TicketCategoryService categories) : ControllerBase
{
    [HttpGet(Name = "ListTicketCategories")]
    [Authorize(Policy = AuthPolicies.TenantMember)]
    [EndpointSummary("List ticket categories")]
    [EndpointDescription("Returns active categories; Owners and Managers may include inactive ones. Requires an active membership in the tenant; Customers only see their own tickets.")]
    public async Task<ActionResult<IReadOnlyList<TicketCategoryDto>>> ListTicketCategories(CancellationToken ct, bool includeInactive = false)
    {
        return Ok(await categories.ListAsync(includeInactive, ct));
    }

    [HttpPost(Name = "CreateTicketCategory")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Create ticket category")]
    [EndpointDescription("Creates a category with a tenant-unique name. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<TicketCategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketCategoryDto>> CreateTicketCategory(Guid tenantId, CategoryRequest request, CancellationToken ct)
    {
        var category = await categories.CreateAsync(request.Name, ct);
        return Created($"/api/v1/tenants/{tenantId}/ticket-categories/{category.Id}", category);
    }

    [HttpPut("{categoryId:guid}", Name = "RenameTicketCategory")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Rename ticket category")]
    [EndpointDescription("Renames a category; names stay unique per tenant. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<TicketCategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketCategoryDto>> RenameTicketCategory(Guid categoryId, CategoryRequest request, CancellationToken ct)
    {
        return Ok(await categories.RenameAsync(categoryId, request.Name, ct));
    }

    [HttpPut("{categoryId:guid}/status", Name = "SetTicketCategoryStatus")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Activate or deactivate category")]
    [EndpointDescription("Deactivated categories cannot be used for new tickets; existing tickets keep them. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetTicketCategoryStatus(Guid categoryId, SetStatusRequest request, CancellationToken ct)
    {
        await categories.SetActiveAsync(categoryId, request.IsActive, ct);
        return NoContent();
    }
}
