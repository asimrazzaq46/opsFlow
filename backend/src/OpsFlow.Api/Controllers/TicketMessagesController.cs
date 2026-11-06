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
public sealed class TicketMessagesController(TicketMessageService messages, TicketChatCursorService cursors)
    : ControllerBase
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
        var result = await messages.ListAsync(ticketId, afterSequence, page, pageSize, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
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
        if (result.Result is ObjectResult failure)
        {
            return this.Answer(failure);
        }

        var sent = result.Value!;
        return sent.Created
            ? Created(
                $"/api/v1/tenants/{tenantId}/tickets/{ticketId}/messages?afterSequence={sent.Value.Sequence - 1}",
                sent.Value
            )
            : Ok(sent.Value);
    }

    [HttpPost("cursor", Name = "UpdateTicketMessageCursor")]
    [EndpointSummary("Acknowledge ticket messages")]
    [EndpointDescription(
        "Moves the caller's own conversation cursor forward: deliveredSequence when messages were received, readSequence when they were displayed (reading also counts as delivery). Each value must be the sequence of a stored chat message of this ticket; a cursor never moves backwards, so repeating or reordering acknowledgements is harmless. Returns the caller's cursor. Requires the same access as reading the conversation."
    )]
    [ProducesResponseType<TicketConversationCursorDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketConversationCursorDto>> UpdateTicketMessageCursor(
        Guid ticketId,
        AcknowledgeMessagesRequest request,
        CancellationToken ct
    )
    {
        var result = await cursors.AcknowledgeAsync(
            ticketId,
            new AcknowledgeMessagesCommand(request.DeliveredSequence, request.ReadSequence),
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }
}
