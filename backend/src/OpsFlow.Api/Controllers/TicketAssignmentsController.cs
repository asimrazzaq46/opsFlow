using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets/{ticketId:guid}/assignments")]
[Tags("Tickets")]
[Authorize(Policy = AuthPolicies.TenantMember)]
public sealed class TicketAssignmentsController(TicketAssignmentService assignments)
    : ControllerBase
{
    [HttpGet(Name = "ListTicketAssignments")]
    [EndpointSummary("List ticket assignments")]
    [EndpointDescription(
        "Returns current assignees, or the complete history when includeHistory=True. Requires an Owner, Manager or Agent role in the tenant."
    )]
    public async Task<ActionResult<IReadOnlyList<AssignmentHistoryDto>>> ListTicketAssignments(
        Guid ticketId,
        CancellationToken ct,
        bool includeHistory = false
    )
    {
        var result = await assignments.ListAsync(ticketId, includeHistory, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPost(Name = "AssignTicketAgents")]
    [EndpointSummary("Assign agents")]
    [EndpointDescription(
        "Adds one or more eligible agents (active Agents of this tenant) to the ticket without removing anyone. Owners and Managers may assign anyone; an Agent may add themselves (claim) or a teammate. Customers are rejected. Removing an assignee stays with Owners and Managers."
    )]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketDetailDto>> AssignTicketAgents(
        Guid ticketId,
        AssignAgentsRequest request,
        CancellationToken ct
    )
    {
        var result = await assignments.AssignAsync(ticketId, request.MembershipIds, request.Version, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpDelete("{membershipId:guid}", Name = "UnassignTicketAgent")]
    [EndpointSummary("Unassign agent")]
    [EndpointDescription(
        "Closes an agent's assignment (history is kept). The last eligible assignee cannot be removed while the status needs one. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketDetailDto>> UnassignTicketAgent(
        Guid ticketId,
        Guid membershipId,
        CancellationToken ct,
        int? version = null
    )
    {
        var result = await assignments.UnassignAsync(ticketId, membershipId, version, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }
}
