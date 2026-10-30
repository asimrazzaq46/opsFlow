using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
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
        categories.MapGet("/", ListCategories).WithName("ListTicketCategories").RequireAuthorization(AuthPolicies.TenantMember);
        categories.MapPost("/", CreateCategory).WithName("CreateTicketCategory").RequireAuthorization(AuthPolicies.TenantManager);
        categories.MapPut("/{categoryId:guid}", RenameCategory).WithName("RenameTicketCategory").RequireAuthorization(AuthPolicies.TenantManager);
        categories.MapPut("/{categoryId:guid}/status", SetCategoryStatus).WithName("SetTicketCategoryStatus").RequireAuthorization(AuthPolicies.TenantManager);

        var sla = tenant.MapGroup("/sla-policies").WithTags("SlaPolicies");
        sla.MapGet("/", ListPolicies).WithName("ListSlaPolicies").RequireAuthorization(AuthPolicies.TenantStaff);
        sla.MapPost("/", CreatePolicy).WithName("CreateSlaPolicy").RequireAuthorization(AuthPolicies.TenantManager);
        sla.MapPut("/{policyId:guid}", UpdatePolicy).WithName("UpdateSlaPolicy").RequireAuthorization(AuthPolicies.TenantManager);
        sla.MapPut("/{policyId:guid}/status", SetPolicyStatus).WithName("SetSlaPolicyStatus").RequireAuthorization(AuthPolicies.TenantManager);

        tenant.MapPost("/defaults", InitializeDefaults).WithTags("Tenants").WithName("InitializeTenantDefaults").RequireAuthorization(AuthPolicies.TenantOwner);
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
