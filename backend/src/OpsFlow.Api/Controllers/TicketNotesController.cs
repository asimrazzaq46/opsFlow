using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets/{ticketId:guid}/notes")]
[Authorize(Policy = AuthPolicies.TenantStaff)] // Customers are rejected before any note data is touched
[Tags("Ticket notes")]
public sealed class TicketNotesController(TicketNoteService notes) : ControllerBase
{
    [HttpGet(Name = "ListTicketNotes")]
    [EndpointSummary("List internal notes")]
    [EndpointDescription("Returns the staff-only internal notes of a ticket, newest first and paged. Requires an Owner, Manager or Agent role who may access the ticket; Customers are always rejected.")]
    public async Task<ActionResult<PagedResult<TicketNoteDto>>> ListTicketNotes(Guid ticketId, CancellationToken ct, int page = 1, int pageSize = 50)
    {
        return Ok(await notes.ListAsync(ticketId, page, pageSize, ct));
    }

    [HttpPost(Name = "CreateTicketNote")]
    [EndpointSummary("Add internal note")]
    [EndpointDescription("Adds an immutable staff-only note; the author and time are assigned by the server and the note is delivered live to authorized staff only. Repeating a request with the same clientNoteId returns the original note with 200. Requires an Owner, Manager or Agent role who may access the ticket.")]
    [ProducesResponseType<TicketNoteDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<TicketNoteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<TicketNoteDto>> CreateTicketNote(Guid tenantId, Guid ticketId, CreateNoteRequest request, CancellationToken ct)
    {
        var result = await notes.CreateAsync(ticketId, new CreateNoteCommand(request.Content, request.ClientNoteId!.Value), ct);
        return result.Created
            ? Created($"/api/v1/tenants/{tenantId}/tickets/{ticketId}/notes", result.Value)
            : Ok(result.Value);
    }
}
