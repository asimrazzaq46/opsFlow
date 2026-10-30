using System.Text;
using System.Text.RegularExpressions;

namespace OpsFlow.Application.Tickets;

public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Private storage root of the local provider. Empty = a folder next to the application binaries (Development only).</summary>
    public string? RootPath { get; set; }
}

/// <summary>Storage provider port. Keys are server-generated and opaque; callers never pass client-controlled paths.</summary>
public interface IAttachmentStorage
{
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>Null when the key is malformed or the file is missing.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit allow-list: extension AND leading bytes must agree, the client-supplied MIME type is ignored, and the stored content type
/// comes from here. shortcut: no malware scanning - signature checks do not prove a file is safe; add a scanner before opening uploads to untrusted parties.
/// </summary>
public static partial class AttachmentValidator
{
    private static readonly Dictionary<string, (string ContentType, Func<byte[], bool> Matches)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ("image/png", h => Starts(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)),
        [".jpg"] = ("image/jpeg", h => Starts(h, 0xFF, 0xD8, 0xFF)),
        [".jpeg"] = ("image/jpeg", h => Starts(h, 0xFF, 0xD8, 0xFF)),
        [".gif"] = ("image/gif", h => Starts(h, 0x47, 0x49, 0x46, 0x38)),
        [".pdf"] = ("application/pdf", h => Starts(h, 0x25, 0x50, 0x44, 0x46, 0x2D)),
        [".txt"] = ("text/plain", IsPlainText),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", IsZip),
        [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", IsZip),
    };

    public const int HeaderLength = 8192;

    public static string AllowedExtensions => string.Join(", ", Allowed.Keys);

    /// <summary>Returns the verified content type, or null when the extension is not allowed or the content does not match it.</summary>
    public static string? Verify(string fileName, byte[] header) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var rule) && header.Length > 0 && rule.Matches(header) ? rule.ContentType : null;

    /// <summary>Display name only: no directories, no control characters, bounded length. It never influences the storage path.</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = UnsafeChars().Replace(name, "_").Trim().Trim('.');
        if (name.Length > 150)
        {
            var ext = Path.GetExtension(name);
            name = name[..(150 - ext.Length)] + ext;
        }

        return name;
    }

    private static bool IsZip(byte[] h) => Starts(h, 0x50, 0x4B, 0x03, 0x04);

    private static bool Starts(byte[] header, params byte[] prefix) => header.AsSpan().StartsWith(prefix.AsSpan());

    private static bool IsPlainText(byte[] h)
    {
        if (h.Contains((byte)0))
        {
            return false;
        }

        try
        {
            // a multi-byte character may be cut at the buffer end: drop up to 3 trailing bytes before the strict decode
            for (var trim = 0; trim <= 3 && trim < h.Length; trim++)
            {
                try
                {
                    _ = new UTF8Encoding(false, true).GetString(h, 0, h.Length - trim);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    // try again with fewer bytes
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    [GeneratedRegex(@"[\p{C}<>:""|?*/\\]")]
    private static partial Regex UnsafeChars();
}
