using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Organizations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Endpoints;

internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var orgs = app.MapGroup("/api/v1/organizations").WithTags("Organizations");
        orgs.MapGet("/", ListMine).WithName("ListMyOrganizations")
            .WithSummary("List user organizations")
            .WithDescription("Returns organizations where the authenticated user has an active membership.");
        orgs.MapPost("/", Create).WithName("CreateOrganization")
            .WithSummary("Create organization")
            .WithDescription("Creates a tenant with the caller as its first Owner and seeds the default ticket settings, categories and SLA policies.");

        app.MapPost("/api/v1/invitations/accept", AcceptInvitation)
            .WithTags("Invitations").WithName("AcceptInvitation")
                .WithSummary("Accept invitation")
                .WithDescription("Accepts a tenant invitation with the one-time token; the signed-in user's verified email must match the invited address.")
                .ProducesConflict()
                .RequireRateLimiting(RateLimitPolicies.Sensitive);

        var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").WithTags("Tenants");
        tenant.MapGet("/", GetCurrent).WithName("GetTenant")
            .WithSummary("Get organization")
            .WithDescription("Returns the tenant identified by the route and the caller's role in it. Requires an active membership in the tenant; Customers only see their own tickets.")
            .RequireAuthorization(AuthPolicies.TenantMember);

        tenant.MapGet("/members", ListMembers).WithName("ListMembers")
            .WithSummary("List members")
            .WithDescription("Returns the tenant's memberships, paged (pageSize is capped). Requires an Owner, Manager or Agent role in the tenant.")
            .RequireAuthorization(AuthPolicies.TenantStaff);
        tenant.MapPut("/members/{membershipId:guid}/role", ChangeRole).WithName("ChangeMemberRole")
            .WithSummary("Change member role")
            .WithDescription("Changes a member's role. A tenant always keeps at least one usable Owner. Requires the Owner role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantOwner);
        tenant.MapPut("/members/{membershipId:guid}/status", SetStatus).WithName("SetMemberStatus")
            .WithSummary("Activate or suspend member")
            .WithDescription("Activates or suspends a membership. A tenant always keeps at least one usable Owner. Requires the Owner role in the tenant.")
            .ProducesConflict()
            .RequireAuthorization(AuthPolicies.TenantOwner);

        var invitations = tenant.MapGroup("/invitations").WithTags("Invitations").RequireAuthorization(AuthPolicies.TenantManager);
        invitations.MapGet("/", ListInvitations).WithName("ListInvitations")
            .WithSummary("List pending invitations")
            .WithDescription("Returns the tenant's pending invitations. Requires the Owner or Manager role in the tenant.");
        invitations.MapPost("/", CreateInvitation).WithName("CreateInvitation")
            .WithSummary("Invite a user")
            .WithDescription("Creates an invitation and emails a one-time token. Managers may invite Agents and Customers only. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict()
            .RequireRateLimiting(RateLimitPolicies.Sensitive);
        invitations.MapDelete("/{invitationId:guid}", RevokeInvitation).WithName("RevokeInvitation")
            .WithSummary("Revoke invitation")
            .WithDescription("Revokes a pending invitation. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict();
        return app;
    }

    private static async Task<Ok<IReadOnlyList<OrganizationDto>>> ListMine(ICurrentUser user, OrganizationService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListMineAsync(user.UserId, ct));

    private static async Task<Created<OrganizationDto>> Create(CreateOrganizationRequest request, ICurrentUser user, OrganizationService service, CancellationToken ct)
    {
        var org = await service.CreateAsync(user.UserId, request.Name, ct);
        return TypedResults.Created($"/api/v1/tenants/{org.Id}", org);
    }

    private static async Task<Ok<OrganizationDto>> AcceptInvitation(AcceptInvitationRequest request, InvitationService service, CancellationToken ct) =>
        TypedResults.Ok(await service.AcceptAsync(request.Token, ct));

    private static async Task<Ok<OrganizationDto>> GetCurrent(Guid tenantId, OrganizationService service, CancellationToken ct) =>
        TypedResults.Ok(await service.GetCurrentAsync(ct));

    private static async Task<Ok<OpsFlow.Application.Common.PagedResult<MemberDto>>> ListMembers(
        Guid tenantId, MembershipService service, CancellationToken ct, int page = 1, int pageSize = 25) =>
        TypedResults.Ok(await service.ListAsync(page, pageSize, ct));

    private static async Task<NoContent> ChangeRole(Guid tenantId, Guid membershipId, ChangeRoleRequest request, MembershipService service, CancellationToken ct)
    {
        await service.ChangeRoleAsync(membershipId, request.Role, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetStatus(Guid tenantId, Guid membershipId, SetStatusRequest request, MembershipService service, CancellationToken ct)
    {
        await service.SetActiveAsync(membershipId, request.IsActive, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<InvitationDto>>> ListInvitations(Guid tenantId, InvitationService service, CancellationToken ct) =>
        TypedResults.Ok(await service.ListPendingAsync(ct));

    private static async Task<Created<InvitationDto>> CreateInvitation(Guid tenantId, CreateInvitationRequest request, InvitationService service, CancellationToken ct)
    {
        var invitation = await service.CreateAsync(request.Email, request.Role, ct);
        return TypedResults.Created($"/api/v1/tenants/{tenantId}/invitations/{invitation.Id}", invitation);
    }

    private static async Task<NoContent> RevokeInvitation(Guid tenantId, Guid invitationId, InvitationService service, CancellationToken ct)
    {
        await service.RevokeAsync(invitationId, ct);
        return TypedResults.NoContent();
    }
}
