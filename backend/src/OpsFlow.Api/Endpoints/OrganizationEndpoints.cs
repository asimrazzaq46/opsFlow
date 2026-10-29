using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
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
        orgs.MapGet("/", ListMine).WithName("ListMyOrganizations");
        orgs.MapPost("/", Create).WithName("CreateOrganization");

        app.MapPost("/api/v1/invitations/accept", AcceptInvitation)
            .WithTags("Invitations").WithName("AcceptInvitation").RequireRateLimiting(RateLimitPolicies.Sensitive);

        var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").WithTags("Tenants");
        tenant.MapGet("/", GetCurrent).WithName("GetTenant").RequireAuthorization(AuthPolicies.TenantMember);

        tenant.MapGet("/members", ListMembers).WithName("ListMembers").RequireAuthorization(AuthPolicies.TenantStaff);
        tenant.MapPut("/members/{membershipId:guid}/role", ChangeRole).WithName("ChangeMemberRole").RequireAuthorization(AuthPolicies.TenantOwner);
        tenant.MapPut("/members/{membershipId:guid}/status", SetStatus).WithName("SetMemberStatus").RequireAuthorization(AuthPolicies.TenantOwner);

        var invitations = tenant.MapGroup("/invitations").WithTags("Invitations").RequireAuthorization(AuthPolicies.TenantManager);
        invitations.MapGet("/", ListInvitations).WithName("ListInvitations");
        invitations.MapPost("/", CreateInvitation).WithName("CreateInvitation").RequireRateLimiting(RateLimitPolicies.Sensitive);
        invitations.MapDelete("/{invitationId:guid}", RevokeInvitation).WithName("RevokeInvitation");
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
