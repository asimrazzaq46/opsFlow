using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Tickets;

/// <summary>Metadata of a stored file. The physical location is only ever <see cref="StorageKey"/> (server-generated), never exposed.</summary>
public sealed class TicketAttachment : ITenantOwned
{
    private TicketAttachment()
    {
        FileName = string.Empty;
        ContentType = string.Empty;
        StorageKey = string.Empty;
        Sha256 = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TicketId { get; private set; }
    public Guid UploadedByUserId { get; private set; }

    /// <summary>Sanitized display name (no path); used only in Content-Disposition.</summary>
    public string FileName { get; private set; }

    /// <summary>Content type decided by the server from the verified file signature, not from the client.</summary>
    public string ContentType { get; private set; }
    public long SizeBytes { get; private set; }
    public string StorageKey { get; private set; }
    public string Sha256 { get; private set; }

    /// <summary>Staff-only file: customers can neither list nor download it.</summary>
    public bool IsInternal { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TicketAttachment Create(
        Guid tenantId,
        Guid ticketId,
        Guid uploadedByUserId,
        string fileName,
        string contentType,
        long sizeBytes,
        string storageKey,
        string sha256,
        bool isInternal,
        DateTime nowUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketId = ticketId,
            UploadedByUserId = uploadedByUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StorageKey = storageKey,
            Sha256 = sha256,
            IsInternal = isInternal,
            CreatedAtUtc = nowUtc,
        };
}
