using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Per-user conversation cursors and the unread counts derived from them. The user is always the authenticated caller, access follows the
/// chat read rule (<see cref="TicketAccess.ChatScope"/>), and cursors only move forward.
/// </summary>
public sealed class TicketChatCursorService(IOpsFlowDbContext db, TicketAccess access, TimeProvider clock)
{
    public async Task<ActionResult<TicketConversationCursorDto>> AcknowledgeAsync(
        Guid ticketId,
        AcknowledgeMessagesCommand cmd,
        CancellationToken ct
    )
    {
        if (cmd.DeliveredSequence is null && cmd.ReadSequence is null)
        {
            return new BadRequestObjectResult(new ProblemDetails { Title = "Nothing to acknowledge." });
        }

        if (await access.TryResolveAsync(ct) is not { } actor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var tenantId = await access
            .Visible(actor)
            .Where(t => t.Id == ticketId)
            .Select(t => (Guid?)t.TenantId)
            .SingleOrDefaultAsync(ct);
        if (tenantId is null)
        {
            return new NotFoundObjectResult(new ProblemDetails { Title = "Ticket not found." });
        }

        if (!await access.MayWorkOnAsync(actor, ticketId, ct))
        {
            return ActorAccess.Forbidden(TicketAccess.AgentMustBeAssignedToOpen);
        }

        // Every acknowledged position must be a stored chat message of this ticket: never a note's sequence, never a future value.
        List<int> requested = [.. new[] { cmd.DeliveredSequence, cmd.ReadSequence }.OfType<int>().Distinct()];
        var known = await db.TicketMessages.CountAsync(
            m => m.TicketId == ticketId && requested.Contains(m.Sequence),
            ct
        );
        if (known != requested.Count)
        {
            return new BadRequestObjectResult(new ProblemDetails { Title = "Unknown message sequence." });
        }

        var read = cmd.ReadSequence ?? 0;
        var delivered = Math.Max(cmd.DeliveredSequence ?? 0, read); // reading implies delivery, never the reverse
        var userId = actor.UserId;
        var now = clock.GetUtcNow().UtcDateTime;
        if (await AdvanceAsync(ticketId, userId, delivered, read, now, ct) == 0 && !await Mine(ticketId, userId).AnyAsync(ct))
        {
            db.TicketConversationCursors.Add(
                TicketConversationCursor.Create(tenantId.Value, ticketId, userId, delivered, read, now)
            );
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ConversationCursor))
            {
                // A concurrent first acknowledgement created the row: apply this one on top of it.
                db.ChangeTracker.Clear();
                await AdvanceAsync(ticketId, userId, delivered, read, now, ct);
            }
        }

        return await Mine(ticketId, userId)
            .Select(c => new TicketConversationCursorDto(ticketId, c.DeliveredSequence, c.ReadSequence))
            .SingleAsync(ct);
    }

    /// <summary>Unread public messages over every conversation the caller may read in the current workspace.</summary>
    public async Task<ChatUnreadSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        return new ChatUnreadSummaryDto(await Unread(actor, access.ChatScope(actor)).CountAsync(ct));
    }

    /// <summary>Unread counts of one page of tickets in a single grouped query; tickets without unread messages are absent.</summary>
    public async Task<Dictionary<Guid, int>> CountsAsync(
        TicketActor actor,
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken ct
    ) =>
        await Unread(actor, access.ChatScope(actor).Where(t => ticketIds.Contains(t.Id)))
            .GroupBy(m => m.TicketId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    // Messages after the later of the ticket's baseline and the caller's read cursor (none = 0), excluding the caller's own messages.
    // Internal notes live in another table, so they can never count.
    private IQueryable<TicketMessage> Unread(TicketActor actor, IQueryable<Ticket> scope)
    {
        var userId = actor.UserId;
        return from t in scope
            from m in db.TicketMessages.Where(m => m.TicketId == t.Id)
            let read =
                db.TicketConversationCursors.Where(c => c.TicketId == t.Id && c.UserId == userId)
                    .Select(c => (int?)c.ReadSequence)
                    .FirstOrDefault() ?? 0
            where m.SenderUserId != userId && m.Sequence > t.ChatReadBaselineSequence && m.Sequence > read
            select m;
    }

    private IQueryable<TicketConversationCursor> Mine(Guid ticketId, Guid userId) =>
        db.TicketConversationCursors.Where(c => c.TicketId == ticketId && c.UserId == userId);

    // One conditional UPDATE: the database takes the larger value under its row lock, so concurrent acknowledgements never move a
    // cursor backwards and an acknowledgement that advances nothing writes nothing.
    private Task<int> AdvanceAsync(
        Guid ticketId,
        Guid userId,
        int delivered,
        int read,
        DateTime now,
        CancellationToken ct
    ) =>
        Mine(ticketId, userId)
            .Where(c => c.DeliveredSequence < delivered || c.ReadSequence < read)
            .ExecuteUpdateAsync(
                s =>
                    s.SetProperty(
                            c => c.DeliveredSequence,
                            c => c.DeliveredSequence < delivered ? delivered : c.DeliveredSequence
                        )
                        .SetProperty(c => c.ReadSequence, c => c.ReadSequence < read ? read : c.ReadSequence)
                        .SetProperty(c => c.UpdatedAtUtc, now),
                ct
            );
}
