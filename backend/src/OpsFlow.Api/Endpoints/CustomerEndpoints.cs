using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Customers;

namespace OpsFlow.Api.Endpoints;

internal static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/tenants/{tenantId:guid}/customers").WithTags("Customers").RequireAuthorization(AuthPolicies.TenantManager);

        g.MapGet("/", List).WithName("ListCustomers")
            .WithSummary("List customers")
            .WithDescription("Returns the tenant's customer contacts, paged, with optional search. Requires the Owner or Manager role in the tenant.");
        g.MapPost("/", Create).WithName("CreateCustomer")
            .WithSummary("Create customer")
            .WithDescription("Creates a customer contact (not a login account). Requires the Owner or Manager role in the tenant.")
            .ProducesConflict();
        g.MapGet("/{customerId:guid}", Get).WithName("GetCustomer")
            .WithSummary("Get customer")
            .WithDescription("Returns one customer contact. Requires the Owner or Manager role in the tenant.");
        g.MapPut("/{customerId:guid}", Update).WithName("UpdateCustomer")
            .WithSummary("Update customer")
            .WithDescription("Updates a customer contact. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict();
        g.MapPut("/{customerId:guid}/status", SetStatus).WithName("SetCustomerStatus")
            .WithSummary("Activate or deactivate customer")
            .WithDescription("Changes whether the customer can be selected for new tickets; history is kept. Requires the Owner or Manager role in the tenant.");
        return app;
    }

    private static async Task<Ok<PagedResult<CustomerDto>>> List(Guid tenantId, CustomerService customers, CancellationToken ct, string? search = null, bool? isActive = null, int page = 1, int pageSize = 25) =>
        TypedResults.Ok(await customers.ListAsync(search, isActive, page, pageSize, ct));

    private static async Task<Created<CustomerDto>> Create(Guid tenantId, CustomerRequest request, CustomerService customers, CancellationToken ct)
    {
        var customer = await customers.CreateAsync(request.Name, request.Email, request.Phone, request.LinkedUserId, ct);
        return TypedResults.Created($"/api/v1/tenants/{tenantId}/customers/{customer.Id}", customer);
    }

    private static async Task<Ok<CustomerDto>> Get(Guid tenantId, Guid customerId, CustomerService customers, CancellationToken ct) =>
        TypedResults.Ok(await customers.GetAsync(customerId, ct));

    private static async Task<Ok<CustomerDto>> Update(Guid tenantId, Guid customerId, CustomerRequest request, CustomerService customers, CancellationToken ct) =>
        TypedResults.Ok(await customers.UpdateAsync(customerId, request.Name, request.Email, request.Phone, request.LinkedUserId, ct));

    private static async Task<NoContent> SetStatus(Guid tenantId, Guid customerId, SetStatusRequest request, CustomerService customers, CancellationToken ct)
    {
        await customers.SetActiveAsync(customerId, request.IsActive, ct);
        return TypedResults.NoContent();
    }
}
