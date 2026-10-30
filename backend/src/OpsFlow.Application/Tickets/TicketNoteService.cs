using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Staff-only internal notes. Every method rejects Customers on the CURRENT database role (not only at the controller), notes live in
/// their own table, and neither the realtime payload nor the notification carries content to anyone but staff who may see the ticket.
/// </summary>
public sealed class TicketNoteService(
    IOpsFlowDbContext db,
    TicketAccess access,
    NotificationService notifications,
    TicketNoteMapper mapper,
    OutboxWriter outbox,
    TimeProvider clock
)
{
    private const int MaxPageSize = 100;

    public async Task<WriteResult<TicketNoteDto>> CreateAsync(
        Guid ticketId,
        CreateNoteCommand cmd,
        CancellationToken ct
    )
    {
        var written = await TryWriteAsync(ticketId, cmd, ct);
        if (written is null)
        {
            db.ChangeTracker.Clear();
            var actor = await access.ResolveAsync(ct);
            var existing = await db
                .TicketNotes.AsNoTracking()
                .SingleAsync(
                    n =>
                        n.TicketId == ticketId
                        && n.AuthorUserId == actor.UserId
                        && n.ClientNoteId == cmd.ClientNoteId,
                    ct
                );
            return new WriteResult<TicketNoteDto>((await MapAsync([existing], ct))[0], false);
        }

        return written;
    }

    private async Task<WriteResult<TicketNoteDto>?> TryWriteAsync(
        Guid ticketId,
        CreateNoteCommand cmd,
        CancellationToken ct
    )
    {
        var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct);
        await using var _ = transaction;
        RequireStaff(actor);
        var ticket =
            await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Ticket not found.");
        var existing = await db
            .TicketNotes.AsNoTracking()
            .SingleOrDefaultAsync(
                n =>
                    n.TicketId == ticketId
                    && n.AuthorUserId == actor.UserId
                    && n.ClientNoteId == cmd.ClientNoteId,
                ct
            );
        if (existing is not null)
        {
            return new WriteResult<TicketNoteDto>((await MapAsync([existing], ct))[0], false);
        }

        var sequence = await access.NextMessageSequenceAsync(ticketId, ct);
        var note = TicketNote.Create(
            ticket.TenantId,
            ticketId,
            sequence,
            actor.UserId,
            cmd.Content,
            cmd.ClientNoteId,
            clock.GetUtcNow().UtcDateTime
        );
        db.TicketNotes.Add(note);
        outbox.Stage(
            ticket.TenantId,
            OutboxEvents.TicketNoteCreated,
            ticketId,
            new TicketNoteEvent(ticketId, note.Id),
            $"note:{note.Id}",
            OutboxChannels.Realtime
        ); // UPDATED: staff push outbox se
        await notifications.StageAsync(
            ticket,
            NotificationType.NoteAdded,
            actor.UserId,
            $"note:{note.Id}",
            toStaff: true,
            toCustomer: false,
            ct: ct
        );
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.NoteClientId)
                || db.IsUniqueViolation(ex, UniqueConstraintKind.OutboxDedup)
            )
        {
            return null;
        }
        catch (Exception ex) when (db.IsDeadlock(ex))
        {
            throw AppException.Conflict("The ticket was changed by someone else.", "Please retry.");
        }

        // UPDATED: commit ho chuka; staff push outbox dispatcher karta hai
        return new WriteResult<TicketNoteDto>(
            (await MapAsync([note], CancellationToken.None))[0],
            true
        );
    }

    public async Task<PagedResult<TicketNoteDto>> ListAsync(
        Guid ticketId,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
        RequireStaff(actor);
        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw AppException.NotFound("Ticket not found.");
        }

        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = db.TicketNotes.AsNoTracking().Where(n => n.TicketId == ticketId);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.Sequence)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<TicketNoteDto>(await MapAsync(rows, ct), page, pageSize, total);
    }

    private static void RequireStaff(TicketActor actor)
    {
        if (actor.IsCustomer)
        {
            throw AppException.Forbidden("Internal notes are not available to customers.");
        }
    }

    private Task<List<TicketNoteDto>> MapAsync(
        IReadOnlyCollection<TicketNote> notes,
        CancellationToken ct
    ) => mapper.MapAsync(notes, ct);
}

/// <summary>Maps stored notes (staff only) with the author's display name.</summary>
public sealed class TicketNoteMapper(IOpsFlowDbContext db)
{
    public async Task<List<TicketNoteDto>> MapAsync(
        IReadOnlyCollection<TicketNote> notes,
        CancellationToken ct
    )
    {
        var ids = notes.Select(n => n.AuthorUserId).Distinct().ToList();
        var names = await db
            .Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return notes
            .Select(n => new TicketNoteDto(
                n.Id,
                n.TicketId,
                n.Sequence,
                n.AuthorUserId,
                names.GetValueOrDefault(n.AuthorUserId) ?? string.Empty,
                n.Content,
                n.CreatedAtUtc
            ))
            .ToList();
    }
}
