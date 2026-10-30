using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
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
    public async Task<AttachmentDto> UploadAsync(
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
            throw AppException.BadRequest("The file is empty.");
        }

        if (content.Length > max)
        {
            throw AppException
                .Validation("file", $"The file exceeds the maximum size of {max} bytes.")
                .WithCode("ATTACHMENT_TOO_LARGE");
        }

        var safeName = AttachmentValidator.SanitizeFileName(fileName);
        var header = new byte[(int)Math.Min(content.Length, AttachmentValidator.HeaderLength)];
        await content.ReadExactlyAsync(header, ct);
        var contentType = safeName.Length > 0 ? AttachmentValidator.Verify(safeName, header) : null;
        if (contentType is null)
        {
            throw AppException
                .Validation(
                    "file",
                    $"Only {AttachmentValidator.AllowedExtensions} files whose content matches their type are accepted."
                )
                .WithCode("ATTACHMENT_TYPE_NOT_ALLOWED");
        }

        content.Position = 0;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
        content.Position = 0;

        // Authorize BEFORE touching storage so an unauthorized caller cannot create files at all.
        var preActor = await access.ResolveAsync(ct);
        RequireMayUpload(preActor, isInternal);
        if (!await access.Visible(preActor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw AppException.NotFound("Ticket not found.");
        }

        var key = await storage.SaveAsync(content, ct);
        try
        {
            var (transaction, actor) = await access.BeginAuthorizedAsync(null, ct);
            await using var _ = transaction;
            RequireMayUpload(actor, isInternal);
            var ticket =
                await access.Visible(actor).Where(t => t.Id == ticketId).SingleOrDefaultAsync(ct)
                ?? throw AppException.NotFound("Ticket not found.");
            if (ticket.Status == TicketStatus.Closed)
            {
                throw AppException.Conflict(
                    "A closed ticket does not accept attachments; reopen it first."
                );
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var attachment = TicketAttachment.Create(
                ticket.TenantId,
                ticketId,
                actor.UserId,
                safeName,
                contentType,
                content.Length,
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
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Please retry."
                );
            }
            catch (DbUpdateException ex)
                when (db.IsUniqueViolation(ex, UniqueConstraintKind.ActivitySequence))
            {
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Please retry."
                );
            }
            catch (Exception ex) when (db.IsDeadlock(ex))
            {
                throw AppException.Conflict(
                    "The ticket was changed by someone else.",
                    "Please retry."
                );
            }

            return ToDto(attachment, actor.IsCustomer);
        }
        catch
        {
            await DeleteQuietlyAsync(key);
            throw;
        }
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid ticketId, CancellationToken ct)
    {
        var actor = await access.ResolveAsync(ct);
        if (!await access.Visible(actor).AnyAsync(t => t.Id == ticketId, ct))
        {
            throw AppException.NotFound("Ticket not found.");
        }

        var rows = await db
            .TicketAttachments.AsNoTracking()
            .Where(a => a.TicketId == ticketId && (!actor.IsCustomer || !a.IsInternal))
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);
        return rows.ConvertAll(a => ToDto(a, actor.IsCustomer));
    }

    public async Task<AttachmentDownload> DownloadAsync(
        Guid ticketId,
        Guid attachmentId,
        CancellationToken ct
    )
    {
        var actor = await access.ResolveAsync(ct);
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
            throw AppException.NotFound("Attachment not found.");
        }

        var stream = await storage.OpenReadAsync(attachment.StorageKey, ct);
        if (stream is null)
        {
            LogMissingFile(logger, attachment.Id);
            throw AppException.NotFound("Attachment not found.");
        }

        return new AttachmentDownload(stream, attachment.FileName, attachment.ContentType);
    }

    private static void RequireMayUpload(TicketActor actor, bool isInternal)
    {
        if (isInternal && actor.IsCustomer)
        {
            throw AppException.Forbidden("Customers cannot upload internal attachments.");
        }
    }

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
