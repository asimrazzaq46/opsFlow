using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tenants;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Endpoints;

/// <summary>Ticket categories, SLA policies and the explicit tenant-defaults initialization.</summary>
internal static class TenantConfigEndpoints
{
    public static IEndpointRouteBuilder MapTenantConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}");

        var categories = tenant.MapGroup("/ticket-categories").WithTags("TicketCategories");
        categories.MapGet("/", ListCategories).WithName("ListTicketCategories")
            .WithSummary("List ticket categories")
            .WithDescription("Returns active categories; Owners and Managers may include inactive ones. Requires an active membership in the tenant; Customers only see their own tickets.")
            .RequireAuthorization(AuthPolicies.TenantMember);
        categories.MapPost("/", CreateCategory).WithName("CreateTicketCategory")
            .WithSummary("Create ticket category")
            .WithDescription("Creates a category with a tenant-unique name. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantManager);
        categories.MapPut("/{categoryId:guid}", RenameCategory).WithName("RenameTicketCategory")
            .WithSummary("Rename ticket category")
            .WithDescription("Renames a category; names stay unique per tenant. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantManager);
        categories.MapPut("/{categoryId:guid}/status", SetCategoryStatus).WithName("SetTicketCategoryStatus")
            .WithSummary("Activate or deactivate category")
            .WithDescription("Deactivated categories cannot be used for new tickets; existing tickets keep them. Requires the Owner or Manager role in the tenant.")
            .RequireAuthorization(AuthPolicies.TenantManager);

        var sla = tenant.MapGroup("/sla-policies").WithTags("SlaPolicies");
        sla.MapGet("/", ListPolicies).WithName("ListSlaPolicies")
            .WithSummary("List SLA policies")
            .WithDescription("Returns the tenant's SLA policies (calendar minutes). Requires an Owner, Manager or Agent role in the tenant.")
            .RequireAuthorization(AuthPolicies.TenantStaff);
        sla.MapPost("/", CreatePolicy).WithName("CreateSlaPolicy")
            .WithSummary("Create SLA policy")
            .WithDescription("Creates the active policy of a priority; each priority has exactly one active policy. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantManager);
        sla.MapPut("/{policyId:guid}", UpdatePolicy).WithName("UpdateSlaPolicy")
            .WithSummary("Update SLA policy targets")
            .WithDescription("Changes response/resolution targets for future SLA cycles; existing cycles keep their snapshot. Requires the Owner or Manager role in the tenant.")
            .RequireAuthorization(AuthPolicies.TenantManager);
        sla.MapPut("/{policyId:guid}/status", SetPolicyStatus).WithName("SetSlaPolicyStatus")
            .WithSummary("Activate SLA policy")
            .WithDescription("A priority must always keep an active policy, so an active policy cannot be deactivated (409): edit its targets instead. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantManager);

        tenant.MapPost("/defaults", InitializeDefaults).WithTags("Tenants").WithName("InitializeTenantDefaults")
            .WithSummary("Initialize tenant defaults")
            .WithDescription("Idempotently adds the missing default ticket settings, counter, categories and SLA policies (for tenants created before Milestone 2). Requires the Owner role in the tenant.")
            .RequireAuthorization(AuthPolicies.TenantOwner);
        return app;
    }

    private static async Task<Ok<IReadOnlyList<TicketCategoryDto>>> ListCategories(Guid tenantId, TicketCategoryService service, CancellationToken ct, bool includeInactive = false) =>
        TypedResults.Ok(await service.ListAsync(includeInactive, ct));

    private static async Task<Created<TicketCategoryDto>> CreateCategory(Guid tenantId, CategoryRequest request, TicketCategoryService service, CancellationToken ct)
    {
        var category = await service.CreateAsync(request.Name, ct);
        return TypedResults.Created($"/api/v1/tenants/{tenantId}/ticket-categories/{category.Id}", category);
    }

    private static async Task<Ok<TicketCategoryDto>> RenameCategory(Guid tenantId, Guid categoryId, CategoryRequest request, TicketCategoryService service, CancellationToken ct) =>
        TypedResults.Ok(await service.RenameAsync(categoryId, request.Name, ct));

    private static async Task<NoContent> SetCategoryStatus(Guid tenantId, Guid categoryId, SetStatusRequest request, TicketCategoryService service, CancellationToken ct)
    {
        await service.SetActiveAsync(categoryId, request.IsActive, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<SlaPolicyDto>>> ListPolicies(Guid tenantId, SlaPolicyService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListAsync(ct));

    private static async Task<Created<SlaPolicyDto>> CreatePolicy(Guid tenantId, CreateSlaPolicyRequest request, SlaPolicyService service, CancellationToken ct)
    {
        var policy = await service.CreateAsync(request.Priority, request.ResponseMinutes, request.ResolutionMinutes, ct);
        return TypedResults.Created($"/api/v1/tenants/{tenantId}/sla-policies/{policy.Id}", policy);
    }

    private static async Task<Ok<SlaPolicyDto>> UpdatePolicy(Guid tenantId, Guid policyId, UpdateSlaPolicyRequest request, SlaPolicyService service, CancellationToken ct) =>
        TypedResults.Ok(await service.UpdateAsync(policyId, request.ResponseMinutes, request.ResolutionMinutes, ct));

    private static async Task<NoContent> SetPolicyStatus(Guid tenantId, Guid policyId, SetStatusRequest request, SlaPolicyService service, CancellationToken ct)
    {
        await service.SetActiveAsync(policyId, request.IsActive, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Idempotent: adds only the missing defaults (settings, ticket counter, categories, SLA policies). Needed for tenants created before Milestone 2.</summary>
    private static async Task<Ok<DefaultsResult>> InitializeDefaults(Guid tenantId, TenantDefaultsService defaults, CancellationToken ct) =>
        TypedResults.Ok(new DefaultsResult(await defaults.EnsureAsync(ct)));
}

public sealed record DefaultsResult(int RecordsAdded);
