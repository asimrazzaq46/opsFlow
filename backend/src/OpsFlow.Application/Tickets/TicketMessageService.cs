using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Realtime.Chat;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Customer-visible ticket chat. Sending runs through the reusable <see cref="ChatService{TMessage}"/> pipeline (validate -> authorize ->
/// <see cref="TicketChatStore"/> commit -> best-effort realtime push); a failed push never undoes a committed message, clients recover through
/// <see cref="ListAsync"/> (afterSequence).
/// </summary>
public sealed class TicketMessageService(IOpsFlowDbContext db, TicketAccess access, TicketMessageMapper mapper, ChatService<TicketMessageDto> chat, ICurrentUser currentUser)
{
    private const int MaxPageSize = 100;

    // UPDATED: pehle ye service seedha store+publish karti thi; ab generic ChatService pipeline use hoti hai (sender sirf authenticated user)
    public async Task<WriteResult<TicketMessageDto>> SendAsync(Guid ticketId, SendMessageCommand cmd, CancellationToken ct)
    {
        try
        {
            var result = await chat.SendAsync(ChatUser.FromAuthenticatedHostContext(currentUser.UserId.ToString()), ticketId.ToString(), cmd.ClientMessageId.ToString(), cmd.Content, ct);
            return new WriteResult<TicketMessageDto>(result.Message, !result.Duplicate);
        }
        catch (ChatException ex)
        {
            throw ex.Failure switch
            {
                ChatFailure.Forbidden => AppException.NotFound("Ticket not found."), // same answer as before: no hint that the ticket exists
                ChatFailure.Unauthenticated => AppException.Unauthorized("Authentication is required."),
                _ => AppException.BadRequest(ex.Message),
            };
        }
    }

    /// <summary>
    /// Newest-first page by default; with <paramref name="afterSequence"/> the messages newer than that sequence in ascending order
    /// (reconnect sync). Deterministic: Sequence is unique per ticket.
    /// </summary>
    public async Task<PagedResult<TicketMessageDto>> ListAsync(Guid ticketId, int? afterSequence, int page, int pageSize, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw AppException.NotFound("Ticket not found.");
        }

        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = db.TicketMessages.AsNoTracking().Where(m => m.TicketId == ticketId);
        if (afterSequence is { } after)
        {
            query = query.Where(m => m.Sequence > after);
        }

        var total = await query.CountAsync(ct);
        var rows = await (afterSequence is null ? query.OrderByDescending(m => m.Sequence) : query.OrderBy(m => m.Sequence)).Skip(skip).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TicketMessageDto>(await mapper.MapAsync(rows, actor.IsCustomer, ct), page, pageSize, total);
    }
}
