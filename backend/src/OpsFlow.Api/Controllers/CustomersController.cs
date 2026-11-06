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
    [EndpointDescription(
        "Returns the tenant's customer contacts, paged, with optional search. Requires the Owner or Manager role in the tenant."
    )]
    public async Task<ActionResult<PagedResult<CustomerDto>>> ListCustomers(
        CancellationToken ct,
        string? search = null,
        bool? isActive = null,
        int page = 1,
        int pageSize = 25,
        string? sortBy = null,
        bool descending = false
    )
    {
        return Ok(await customers.ListAsync(search, isActive, page, pageSize, ct, sortBy, descending));
    }

    [HttpPost(Name = "CreateCustomer")]
    [EndpointSummary("Create customer")]
    [EndpointDescription(
        "Creates a customer contact (not a login account). Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<CustomerDto>> CreateCustomer(
        Guid tenantId,
        CustomerRequest request,
        CancellationToken ct
    )
    {
        var result = await customers.CreateAsync(
            request.Name,
            request.Email,
            request.Phone,
            request.LinkedUserId,
            ct
        );
        if (result.Result is ObjectResult failure)
        {
            return this.Answer(failure);
        }

        var customer = result.Value!;
        return Created($"/api/v1/tenants/{tenantId}/customers/{customer.Id}", customer);
    }

    [HttpGet("{customerId:guid}", Name = "GetCustomer")]
    [EndpointSummary("Get customer")]
    [EndpointDescription(
        "Returns one customer contact. Requires the Owner or Manager role in the tenant."
    )]
    public async Task<ActionResult<CustomerDto>> GetCustomer(Guid customerId, CancellationToken ct)
    {
        return Ok(await customers.GetAsync(customerId, ct));
    }

    [HttpPut("{customerId:guid}", Name = "UpdateCustomer")]
    [EndpointSummary("Update customer")]
    [EndpointDescription(
        "Renames a customer contact and optionally links a portal user. Email and phone cannot be changed here (they belong to the account owner); sending different values returns 403. A null linkedUserId keeps the current link; use the unlink action to remove it. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(
        Guid customerId,
        CustomerUpdateRequest request,
        CancellationToken ct
    )
    {
        var result = await customers.UpdateAsync(
            customerId,
            request.Name,
            request.Email,
            request.Phone,
            request.LinkedUserId,
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPut("{customerId:guid}/status", Name = "SetCustomerStatus")]
    [EndpointSummary("Activate or deactivate customer")]
    [EndpointDescription(
        "Allows or stops new tickets for the customer; sign-in and history are unaffected. Requires the Owner role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetCustomerStatus(
        Guid customerId,
        SetStatusRequest request,
        CancellationToken ct
    )
    {
        return await customers.SetActiveAsync(customerId, request.IsActive, ct) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }

    [HttpDelete("{customerId:guid}/user", Name = "UnlinkCustomerUser")]
    [EndpointSummary("Unlink customer portal user")]
    [EndpointDescription(
        "Removes the portal-user link from a customer contact; the contact and its tickets are kept. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnlinkCustomerUser(Guid customerId, CancellationToken ct)
    {
        return await customers.UnlinkUserAsync(customerId, ct) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }
}
