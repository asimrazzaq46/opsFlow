using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tickets;
using OpsFlow.Realtime.Chat;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// OpsFlow's side of the generic chat pipeline (conversation id = ticket id). Authorization comes from the database (ticket visibility,
/// active membership), the sender must be the authenticated caller, and the dispatcher delivers one view per audience:
/// staff get the full view, the linked customer a view without staff identities. Persistence, sequence, idempotency, SLA first response and
/// notification rows AND the outbox event are ONE authorized transaction; the realtime push is done afterwards by the outbox dispatcher.
/// </summary>
public sealed class TicketChatStore(IOpsFlowDbContext db, TicketAccess access, NotificationService notifications, TicketMessageMapper mapper, OutboxWriter outbox, TimeProvider clock)
    : IChatConversationAuthorizer, IChatMessageStore<TicketMessageDto>
{
    public async ValueTask<bool> IsAllowedAsync(ChatUser user, string conversationId, ChatOperation operation, CancellationToken cancellationToken)
    {
        var actor = await access.ResolveAsync(cancellationToken); // suspended/inactive actors fail here with the usual 403
        return actor.UserId.ToString() == user.Id
            && Guid.TryParse(conversationId, out var ticketId)
            && await access.Visible(actor).AnyAsync(t => t.Id == ticketId, cancellationToken);
    }

    public async Task<ChatAppendResult<TicketMessageDto>> AppendAsync(ChatSendRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ConversationId, out var ticketId) || !Guid.TryParse(request.ClientMessageId, out var clientMessageId))
        {
            throw new ChatException(ChatFailure.Invalid, "Invalid conversation or client message id.");
        }

        var written = await TryWriteAsync(ticketId, clientMessageId, request, cancellationToken);
        if (written is not null)
        {
            return written;
        }

        // Lost an idempotency race against the same ClientMessageId: the winner's row is the answer.
        db.ChangeTracker.Clear();
        var actor = await access.ResolveAsync(cancellationToken);
        var existing = await db.TicketMessages.AsNoTracking().SingleAsync(m => m.TicketId == ticketId && m.SenderUserId == actor.UserId && m.ClientMessageId == clientMessageId, cancellationToken);
        return new ChatAppendResult<TicketMessageDto>((await mapper.MapAsync([existing], actor.IsCustomer, cancellationToken))[0], true, []);
    }

    private async Task<ChatAppendResult<TicketMessageDto>?> TryWriteAsync(Guid ticketId, Guid clientMessageId, ChatSendRequest request, CancellationToken ct)
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct);
        await using var _ = transaction;
        if (actor.UserId.ToString() != request.Sender.Id)
        {
            throw AppException.Forbidden("The sender must be the authenticated user.");
        }

        var ticket = await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Ticket not found.");
        if (ticket.Status == TicketStatus.Closed)
        {
            throw AppException.Conflict("A closed ticket does not accept new messages; reopen it first.");
        }

        var existing = await db.TicketMessages.AsNoTracking().SingleOrDefaultAsync(m => m.TicketId == ticketId && m.SenderUserId == actor.UserId && m.ClientMessageId == clientMessageId, ct);
        if (existing is not null)
        {
            return new ChatAppendResult<TicketMessageDto>((await mapper.MapAsync([existing], actor.IsCustomer, ct))[0], true, []);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var sequence = await access.NextMessageSequenceAsync(ticketId, ct);
        var message = TicketMessage.Create(ticket.TenantId, ticketId, sequence, actor.UserId, !actor.IsCustomer, request.Content, clientMessageId, now);
        db.TicketMessages.Add(message);

        if (!actor.IsCustomer)
        {
            // First customer-visible staff reply completes the response SLA of the ACTIVE initial cycle (atomic, never overwritten).
            await db.TicketSlaCycles.Where(c => c.TicketId == ticketId && c.ResolvedAtUtc == null && c.FirstResponseAtUtc == null && c.Reason == SlaCycleReason.Initial)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FirstResponseAtUtc, (DateTime?)now), ct);
        }

        outbox.Stage(ticket.TenantId, OutboxEvents.TicketMessageCreated, ticketId, new TicketMessageEvent(ticketId, message.Id), $"chat:{message.Id}", OutboxChannels.Realtime); // UPDATED: live push outbox se
        await notifications.StageAsync(ticket, NotificationType.MessageReceived, actor.UserId, $"msg:{message.Id}", toStaff: actor.IsCustomer, toCustomer: !actor.IsCustomer, ct: ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.MessageClientId) || db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup))
        {
            return null;
        }
        catch (Exception ex) when (db.IsDeadlock(ex))
        {
            throw AppException.Conflict("The ticket was changed by someone else.", "Please retry.");
        }

        // UPDATED: deliveries khali: realtime push outbox dispatcher karta hai (duplicate direct publish nahi)
        return new ChatAppendResult<TicketMessageDto>((await mapper.MapAsync([message], actor.IsCustomer, CancellationToken.None))[0], false, []);
    }

}

/// <summary>Maps stored messages to the audience's view (customers never receive staff identities).</summary>
public sealed class TicketMessageMapper(IOpsFlowDbContext db)
{
    public async Task<List<TicketMessageDto>> MapAsync(IReadOnlyCollection<TicketMessage> messages, bool forCustomer, CancellationToken ct)
    {
        var ids = messages.Select(m => m.SenderUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return messages.Select(m => forCustomer && m.SenderIsStaff
                ? new TicketMessageDto(m.Id, m.TicketId, m.Sequence, null, null, true, m.Content, m.CreatedAtUtc)
                : new TicketMessageDto(m.Id, m.TicketId, m.Sequence, m.SenderUserId, names.GetValueOrDefault(m.SenderUserId), m.SenderIsStaff, m.Content, m.CreatedAtUtc))
            .ToList();
    }
}
