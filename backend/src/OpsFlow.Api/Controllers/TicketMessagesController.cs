using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets/{ticketId:guid}/messages")]
[Authorize(Policy = AuthPolicies.TenantMember)] // visibility (Customer = own tickets, Agent = assigned/created) is decided from DB state by the service
[Tags("Ticket messages")]
public sealed class TicketMessagesController(TicketMessageService messages) : ControllerBase
{
    [HttpGet(Name = "ListTicketMessages")]
    [EndpointSummary("List ticket messages")]
    [EndpointDescription(
        "Returns the customer-visible chat of a ticket, newest first and paged; with afterSequence it returns only newer messages in ascending order (use it to catch up after a reconnect). Customers never receive staff identities. Requires an active membership in the tenant."
    )]
    public async Task<ActionResult<PagedResult<TicketMessageDto>>> ListTicketMessages(
        Guid ticketId,
        CancellationToken ct,
        int? afterSequence = null,
        int page = 1,
        int pageSize = 50
    )
    {
        return Ok(await messages.ListAsync(ticketId, afterSequence, page, pageSize, ct));
    }

    [HttpPost(Name = "SendTicketMessage")]
    [EndpointSummary("Send ticket message")]
    [EndpointDescription(
        "Stores a chat message on a ticket the caller may access, then delivers it live to the people allowed to see it. The sender, timestamp and order are assigned by the server. Repeating a request with the same clientMessageId returns the original message with 200 instead of creating a duplicate. Requires an active membership in the tenant."
    )]
    [ProducesResponseType<TicketMessageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<TicketMessageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketMessageDto>> SendTicketMessage(
        Guid tenantId,
        Guid ticketId,
        SendMessageRequest request,
        CancellationToken ct
    )
    {
        var result = await messages.SendAsync(
            ticketId,
            new SendMessageCommand(request.Content, request.ClientMessageId!.Value),
            ct
        );
        return result.Created
            ? Created(
                $"/api/v1/tenants/{tenantId}/tickets/{ticketId}/messages?afterSequence={result.Value.Sequence - 1}",
                result.Value
            )
            : Ok(result.Value);
    }
}
