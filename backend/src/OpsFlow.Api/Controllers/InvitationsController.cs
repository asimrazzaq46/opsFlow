using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Organizations;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class InvitationsController(InvitationService invitations) : ControllerBase
{
    [HttpPost("invitations/accept", Name = "AcceptInvitation")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    [EndpointSummary("Accept invitation")]
    [EndpointDescription(
        "Accepts a tenant invitation with the one-time token; the signed-in user's verified email must match the invited address."
    )]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<OrganizationDto>> AcceptInvitation(
        AcceptInvitationRequest request,
        CancellationToken ct
    )
    {
        return Ok(await invitations.AcceptAsync(request.Token, ct));
    }

    [HttpGet("tenants/{tenantId:guid}/invitations", Name = "ListInvitations")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("List pending invitations")]
    [EndpointDescription(
        "Returns the tenant's pending invitations. Requires the Owner or Manager role in the tenant."
    )]
    public async Task<ActionResult<IReadOnlyList<InvitationDto>>> ListInvitations(
        CancellationToken ct
    )
    {
        return Ok(await invitations.ListPendingAsync(ct));
    }

    [HttpPost("tenants/{tenantId:guid}/invitations", Name = "CreateInvitation")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    [EndpointSummary("Invite a user")]
    [EndpointDescription(
        "Creates an invitation and emails a one-time token. Managers may invite Agents and Customers only. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<InvitationDto>> CreateInvitation(
        Guid tenantId,
        CreateInvitationRequest request,
        CancellationToken ct
    )
    {
        var invitation = await invitations.CreateAsync(request.Email, request.Role, ct);
        return Created($"/api/v1/tenants/{tenantId}/invitations/{invitation.Id}", invitation);
    }

    [HttpDelete(
        "tenants/{tenantId:guid}/invitations/{invitationId:guid}",
        Name = "RevokeInvitation"
    )]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Revoke invitation")]
    [EndpointDescription(
        "Revokes a pending invitation. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<IActionResult> RevokeInvitation(Guid invitationId, CancellationToken ct)
    {
        await invitations.RevokeAsync(invitationId, ct);
        return NoContent();
    }
}
