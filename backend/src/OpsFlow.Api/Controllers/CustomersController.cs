using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Customers;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/customers")]
[Authorize(Policy = AuthPolicies.TenantManager)]
public sealed class CustomersController(CustomerService customers) : ControllerBase
{
    [HttpGet(Name = "ListCustomers")]
    [EndpointSummary("List customers")]
    [EndpointDescription("Returns the tenant's customer contacts, paged, with optional search. Requires the Owner or Manager role in the tenant.")]
    public async Task<ActionResult<PagedResult<CustomerDto>>> ListCustomers(CancellationToken ct, string? search = null, bool? isActive = null, int page = 1, int pageSize = 25)
    {
        return Ok(await customers.ListAsync(search, isActive, page, pageSize, ct));
    }

    [HttpPost(Name = "CreateCustomer")]
    [EndpointSummary("Create customer")]
    [EndpointDescription("Creates a customer contact (not a login account). Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer(Guid tenantId, CustomerRequest request, CancellationToken ct)
    {
        var customer = await customers.CreateAsync(request.Name, request.Email, request.Phone, request.LinkedUserId, ct);
        return Created($"/api/v1/tenants/{tenantId}/customers/{customer.Id}", customer);
    }

    [HttpGet("{customerId:guid}", Name = "GetCustomer")]
    [EndpointSummary("Get customer")]
    [EndpointDescription("Returns one customer contact. Requires the Owner or Manager role in the tenant.")]
    public async Task<ActionResult<CustomerDto>> GetCustomer(Guid customerId, CancellationToken ct)
    {
        return Ok(await customers.GetAsync(customerId, ct));
    }

    [HttpPut("{customerId:guid}", Name = "UpdateCustomer")]
    [EndpointSummary("Update customer")]
    [EndpointDescription("Updates a customer contact. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(Guid customerId, CustomerRequest request, CancellationToken ct)
    {
        return Ok(await customers.UpdateAsync(customerId, request.Name, request.Email, request.Phone, request.LinkedUserId, ct));
    }

    [HttpPut("{customerId:guid}/status", Name = "SetCustomerStatus")]
    [EndpointSummary("Activate or deactivate customer")]
    [EndpointDescription("Changes whether the customer can be selected for new tickets; history is kept. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetCustomerStatus(Guid customerId, SetStatusRequest request, CancellationToken ct)
    {
        await customers.SetActiveAsync(customerId, request.IsActive, ct);
        return NoContent();
    }
}
