using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets")]
[Authorize(Policy = AuthPolicies.TenantMember)] // UPDATED: kaun kya dekh/badal sakta hai, wo services DB state se role-wise tay karti hain
public sealed class TicketsController(TicketService tickets, TicketWorkflowService workflow) : ControllerBase
{
    [HttpPost(Name = "CreateTicket")]
    [EndpointSummary("Create service ticket")]
    [EndpointDescription("Creates a tenant-scoped service ticket, initializes its SLA cycle and records activity. Requires an active membership in the tenant; Customers only see their own tickets.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> CreateTicket(Guid tenantId, CreateTicketRequest request, CancellationToken ct)
    {
        var ticket = await tickets.CreateAsync(new CreateTicketCommand(request.Title, request.Description, request.Priority, request.CategoryId, request.CustomerId, request.AssigneeMembershipIds), ct);
        return Created($"/api/v1/tenants/{tenantId}/tickets/{ticket.Summary.Id}", ticket);
    }

    [HttpGet(Name = "ListTickets")]
    [EndpointSummary("List tickets")]
    [EndpointDescription("Returns the tickets visible to the caller, with filters, sorting and capped paging; staff can filter state=Unstaffed. Requires an active membership in the tenant; Customers only see their own tickets.")]
    public async Task<ActionResult<PagedResult<TicketSummaryDto>>> ListTickets([FromQuery] TicketListQuery q, CancellationToken ct)
    {
        return Ok(await tickets.ListAsync(
            new TicketListFilter(q.Status, q.Priority, q.CategoryId, q.AssignedMembershipId, q.CustomerId, q.CreatedFrom, q.CreatedTo, q.State, q.Search, q.SortBy, q.Descending, q.Page, q.PageSize), ct));
    }

    [HttpGet("{ticketId:guid}", Name = "GetTicket")]
    [EndpointSummary("Get ticket")]
    [EndpointDescription("Returns one visible ticket. Customers never receive assignees, SLA or staff identities. Requires an active membership in the tenant; Customers only see their own tickets.")]
    public async Task<ActionResult<TicketDetailDto>> GetTicket(Guid ticketId, CancellationToken ct)
    {
        return Ok(await tickets.GetAsync(ticketId, ct));
    }

    [HttpPut("{ticketId:guid}", Name = "UpdateTicket")]
    [EndpointSummary("Update ticket")]
    [EndpointDescription("Edits title, description, priority or category, guarded by the optimistic-concurrency version. Requires an active membership in the tenant; Customers only see their own tickets.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> UpdateTicket(Guid ticketId, UpdateTicketRequest request, CancellationToken ct)
    {
        return Ok(await tickets.UpdateAsync(ticketId, new UpdateTicketCommand(request.Title, request.Description, request.Priority, request.CategoryId, request.Version), ct));
    }

    [HttpPost("{ticketId:guid}/status", Name = "ChangeTicketStatus")]
    [EndpointSummary("Change ticket status")]
    [EndpointDescription("Moves the ticket along the allowed workflow transitions. Requires an active membership in the tenant; Customers only see their own tickets.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> ChangeTicketStatus(Guid ticketId, TransitionRequest request, CancellationToken ct)
    {
        return Ok(await workflow.TransitionAsync(ticketId, request.Status, request.Version, ct));
    }

    [HttpPost("{ticketId:guid}/reopen", Name = "ReopenTicket")]
    [EndpointSummary("Reopen ticket")]
    [EndpointDescription("Reopens a resolved or closed ticket and starts a new SLA cycle. Requires an active membership in the tenant; Customers only see their own tickets.")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketDetailDto>> ReopenTicket(Guid ticketId, ReopenRequest request, CancellationToken ct)
    {
        return Ok(await workflow.ReopenAsync(ticketId, request.Version, ct));
    }

    [HttpGet("{ticketId:guid}/activity", Name = "ListTicketActivity")]
    [EndpointSummary("List ticket activity")]
    [EndpointDescription("Returns the append-only ticket timeline, paged. Requires an Owner, Manager or Agent role in the tenant.")]
    public async Task<ActionResult<PagedResult<TicketActivityDto>>> ListTicketActivity(Guid ticketId, CancellationToken ct, int page = 1, int pageSize = 50)
    {
        return Ok(await tickets.GetActivityAsync(ticketId, page, pageSize, ct));
    }

    [HttpGet("{ticketId:guid}/sla", Name = "GetTicketSla")]
    [EndpointSummary("Get ticket SLA")]
    [EndpointDescription("Returns the current SLA cycle and the full cycle history. Requires an Owner, Manager or Agent role in the tenant.")]
    public async Task<ActionResult<TicketSlaDto>> GetTicketSla(Guid ticketId, CancellationToken ct)
    {
        return Ok(await tickets.GetSlaAsync(ticketId, ct));
    }
}
