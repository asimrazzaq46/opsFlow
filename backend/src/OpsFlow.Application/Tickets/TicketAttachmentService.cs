using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Ticket files. Upload: cheap checks (size, extension + signature) -> private storage under a server-generated key -> authorized
/// transaction (actor locked, ticket visibility re-checked) writes metadata + a staff-only timeline entry; any failure after the file was
/// stored deletes it again. Download re-authorizes on every request; Customers can neither list nor fetch internal-only files.
/// </summary>
public sealed partial class TicketAttachmentService(
    IOpsFlowDbContext db,
    TicketAccess access,
    IAttachmentStorage storage,
    IOptions<AttachmentOptions> options,
    TimeProvider clock,
    ILogger<TicketAttachmentService> logger
)
{
    public async Task<ActionResult<AttachmentDto>> UploadAsync(
        Guid ticketId,
        string? fileName,
        Stream content,
        bool isInternal,
        CancellationToken ct
    )
    {
        var max = options.Value.MaxBytes;
        if (!content.CanSeek || content.Length is 0 or < 0)
        {
            return new BadRequestObjectResult(new ProblemDetails { Title = "The file is empty." });
        }

        if (content.Length > max)
        {
            return FileError(
                $"The file exceeds the maximum size of {max} bytes.",
                "ATTACHMENT_TOO_LARGE"
            );
        }

        var safeName = AttachmentValidator.SanitizeFileName(fileName);
        var header = new byte[(int)Math.Min(content.Length, AttachmentValidator.HeaderLength)];
        await content.ReadExactlyAsync(header, ct);
        var contentType = safeName.Length > 0 ? AttachmentValidator.Verify(safeName, header) : null;
        if (contentType is null)
        {
            return FileError(
                $"Only {AttachmentValidator.AllowedExtensions} files whose content matches their type are accepted.",
                "ATTACHMENT_TYPE_NOT_ALLOWED"
            );
        }

        content.Position = 0;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
        content.Position = 0;

        // Authorize BEFORE touching storage so an unauthorized caller cannot create files at all.
        if (await access.TryResolveAsync(ct) is not { } preActor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (MayNotUpload(preActor, isInternal) is { } notAllowed)
        {
            return notAllowed;
        }

        if (!await access.Visible(preActor).AnyAsync(t => t.Id == ticketId, ct))
        {
            return TicketNotFound();
        }

        var key = await storage.SaveAsync(content, ct);
        try
        {
            var stored = await StoreAsync(ticketId, safeName, contentType, content.Length, key, sha, isInternal, ct);
            if (stored.Result is not null)
            {
                await DeleteQuietlyAsync(key); // metadata was not committed: do not leave an orphaned file
            }

            return stored;
        }
        catch
        {
            await DeleteQuietlyAsync(key);
            throw;
        }
    }

    private async Task<ActionResult<AttachmentDto>> StoreAsync(
        Guid ticketId,
        string safeName,
        string contentType,
        long length,
        string key,
        string sha,
        bool isInternal,
        CancellationToken ct
    )
    {
        if (await access.TryBeginAuthorizedAsync(null, ct) is not { } begun)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var (transaction, actor) = begun;
        await using var _ = transaction;
        if (MayNotUpload(actor, isInternal) is { } notAllowed)
        {
            return notAllowed;
        }

        var ticket = await access
            .Visible(actor)
            .Where(t => t.Id == ticketId)
            .SingleOrDefaultAsync(ct);
        if (ticket is null)
        {
            return TicketNotFound();
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "A closed ticket does not accept attachments; reopen it first.",
                }
            );
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var attachment = TicketAttachment.Create(
            ticket.TenantId,
            ticketId,
            actor.UserId,
            safeName,
            contentType,
            length,
            key,
            sha,
            isInternal,
            now
        );
        db.TicketAttachments.Add(attachment);
        db.TicketActivities.Add(
            ticket.Record(
                TicketActivityType.AttachmentAdded,
                actor.UserId,
                now,
                newValue: isInternal ? "internal" : "visible",
                relatedId: attachment.Id
            )
        );
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "The ticket was changed by someone else.",
                    Detail = "Please retry.",
                }
            );
        }
        catch (DbUpdateException ex)
            when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "The ticket was changed by someone else.",
                    Detail = "Please retry.",
                }
            );
        }
        catch (Exception ex) when (db.IsDeadlock(ex))
        {
            return new ConflictObjectResult(
                new ProblemDetails
                {
                    Title = "The ticket was changed by someone else.",
                    Detail = "Please retry.",
                }
            );
        }

        return ToDto(attachment, actor.IsCustomer);
    }

    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> ListAsync(
        Guid ticketId,
        CancellationToken ct
    )
    {
        if (await access.TryResolveAsync(ct) is not { } actor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            return TicketNotFound();
        }

        var rows = await db
            .TicketAttachments.AsNoTracking()
            .Where(a => a.TicketId == ticketId && (!actor.IsCustomer || !a.IsInternal))
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
        return rows.ConvertAll(a => ToDto(a, actor.IsCustomer));
    }

    public async Task<ActionResult<AttachmentDownload>> DownloadAsync(
        Guid ticketId,
        Guid attachmentId,
        CancellationToken ct
    )
    {
        if (await access.TryResolveAsync(ct) is not { } actor)
        {
            return ActorAccess.AccessNoLongerValid();
        }

        var attachment = await (
            from a in db.TicketAttachments.AsNoTracking()
            where
                a.Id == attachmentId
                && a.TicketId == ticketId
                && (!actor.IsCustomer || !a.IsInternal)
            select a
        ).SingleOrDefaultAsync(ct);
        if (attachment is null || !await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            return AttachmentNotFound();
        }

        var stream = await storage.OpenReadAsync(attachment.StorageKey, ct);
        if (stream is null)
        {
            LogMissingFile(logger, attachment.Id);
            return AttachmentNotFound();
        }

        return new AttachmentDownload(stream, attachment.FileName, attachment.ContentType);
    }

    private static ObjectResult? MayNotUpload(TicketActor actor, bool isInternal) =>
        isInternal && actor.IsCustomer
            ? ActorAccess.Forbidden("Customers cannot upload internal attachments.")
            : null;

    private static NotFoundObjectResult TicketNotFound() =>
        new(new ProblemDetails { Title = "Ticket not found." });

    private static NotFoundObjectResult AttachmentNotFound() =>
        new(new ProblemDetails { Title = "Attachment not found." });

    private static BadRequestObjectResult FileError(string message, string code) =>
        new(
            new ValidationProblemDetails(new Dictionary<string, string[]> { ["file"] = [message] })
            {
                Extensions = { ["code"] = code },
            }
        );

    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, ex);
        }
    }

    private static AttachmentDto ToDto(TicketAttachment a, bool forCustomer) =>
        new(
            a.Id,
            a.TicketId,
            a.FileName,
            a.ContentType,
            a.SizeBytes,
            a.IsInternal,
            forCustomer ? null : a.UploadedByUserId,
            a.CreatedAtUtc
        );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Attachment {AttachmentId} metadata exists but its file is missing"
    )]
    private static partial void LogMissingFile(ILogger logger, Guid attachmentId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to clean up an orphaned attachment file"
    )]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
