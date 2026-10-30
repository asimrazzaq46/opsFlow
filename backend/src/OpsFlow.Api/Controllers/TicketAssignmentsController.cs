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
public sealed class TicketAssignmentsController(TicketAssignmentService assignments) : ControllerBase
{
    [HttpGet(Name = "ListTicketAssignments")]
    [EndpointSummary("List ticket assignments")]
    [EndpointDescription("Returns current assignees, or the complete history when includeHistory=True. Requires an Owner, Manager or Agent role in the tenant.")]
    public async Task<ActionResult<IReadOnlyList<AssignmentHistoryDto>>> ListTicketAssignments(Guid ticketId, CancellationToken ct, bool includeHistory = false)
    {
        return Ok(await assignments.ListAsync(ticketId, includeHistory, ct));
    }

    [HttpPost(Name = "AssignTicketAgents")]
    [EndpointSummary("Assign agents")]
    [EndpointDescription("Assigns one or more eligible agents to the ticket. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> AssignTicketAgents(Guid ticketId, AssignAgentsRequest request, CancellationToken ct)
    {
        return Ok(await assignments.AssignAsync(ticketId, request.MembershipIds, request.Version, ct));
    }

    [HttpDelete("{membershipId:guid}", Name = "UnassignTicketAgent")]
    [EndpointSummary("Unassign agent")]
    [EndpointDescription("Closes an agent's assignment (history is kept). The last eligible assignee cannot be removed while the status needs one. Requires the Owner or Manager role in the tenant.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> UnassignTicketAgent(Guid ticketId, Guid membershipId, CancellationToken ct, int? version = null)
    {
        return Ok(await assignments.UnassignAsync(ticketId, membershipId, version, ct));
    }
}
