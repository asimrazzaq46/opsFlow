using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Infrastructure.Storage;

/// <summary>
/// Private filesystem storage (Development / single node). Files are named by a server-generated GUID and sharded by its first two
/// characters; a key that is not exactly 32 hex characters is rejected, so no input can traverse out of the root.
/// shortcut: local disk is not shared between nodes - swap this class for a blob provider when the API scales out.
/// </summary>
internal sealed partial class LocalAttachmentStorage(IOptions<AttachmentOptions> options) : IAttachmentStorage
{
    private readonly string _root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.RootPath)
        ? Path.Combine(AppContext.BaseDirectory, "attachments")
        : options.Value.RootPath);

    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid().ToString("N");
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await content.CopyToAsync(file, cancellationToken);
        }
        catch
        {
            File.Delete(path); // never leave a half-written file behind
            throw;
        }

        return key;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(IsValid(key) && File.Exists(PathFor(key))
            ? new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (IsValid(key))
        {
            File.Delete(PathFor(key));
        }

        return Task.CompletedTask;
    }

    private static bool IsValid(string key) => Key().IsMatch(key);

    private string PathFor(string key) => Path.Combine(_root, key[..2], key);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Key();
}
