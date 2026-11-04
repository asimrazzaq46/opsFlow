using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Realtime.Chat;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Customer-visible ticket chat. Sending runs through the reusable <see cref="ChatService{TMessage}"/> pipeline (validate -> authorize ->
/// <see cref="TicketChatStore"/> commit -> best-effort realtime push); a failed push never undoes a committed message, clients recover through
/// <see cref="ListAsync"/> (afterSequence).
/// </summary>
public sealed class TicketMessageService(
    IOpsFlowDbContext db,
    TicketAccess access,
    TicketMessageMapper mapper,
    ChatService<TicketMessageDto> chat,
    ICurrentUser currentUser
)
{
    private const int MaxPageSize = 100;

    // Runs through the generic ChatService pipeline; the sender is always the authenticated user. The pipeline and TicketChatStore
    // still signal their failures with exceptions (the OpsFlow.Realtime contract), so the library's ChatException is answered here.
    public async Task<ActionResult<WriteResult<TicketMessageDto>>> SendAsync(
        Guid ticketId,
        SendMessageCommand cmd,
        CancellationToken ct
    )
    {
        try
        {
            var result = await chat.SendAsync(
                ChatUser.FromAuthenticatedHostContext(currentUser.UserId.ToString()),
                ticketId.ToString(),
                cmd.ClientMessageId.ToString(),
                cmd.Content,
                ct
            );
            return new WriteResult<TicketMessageDto>(result.Message, !result.Duplicate);
        }
        catch (ChatException ex)
        {
            return ex.Failure switch
            {
                ChatFailure.Forbidden => new NotFoundObjectResult(
                    new ProblemDetails { Title = "Ticket not found." } // no hint that the ticket exists
                ),
                ChatFailure.Unauthenticated => new UnauthorizedObjectResult(
                    new ProblemDetails
                    {
                        Title = "Authentication is required.",
                        Status = StatusCodes.Status401Unauthorized,
                    }
                ),
                _ => new BadRequestObjectResult(new ProblemDetails { Title = ex.Message }),
            };
        }
    }

    /// <summary>
    /// Newest-first page by default; with <paramref name="afterSequence"/> the messages newer than that sequence in ascending order
    /// (reconnect sync). Deterministic: Sequence is unique per ticket.
    /// </summary>
    public async Task<ActionResult<PagedResult<TicketMessageDto>>> ListAsync(
        Guid ticketId,
        int? afterSequence,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        if (await access.TryResolveAsync(ct) is not { } actor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Ticket not found." });
        }

        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = db.TicketMessages.AsNoTracking().Where(m => m.TicketId == ticketId);
        if (afterSequence is { } after)
        {
            query = query.Where(m => m.Sequence > after);
        }

        var total = await query.CountAsync(ct);
        var rows = await (
            afterSequence is null
                ? query.OrderByDescending(m => m.Sequence)
                : query.OrderBy(m => m.Sequence)
        )
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<TicketMessageDto>(
            await mapper.MapAsync(rows, actor.IsCustomer, ct),
            page,
            pageSize,
            total
        );
    }
}
