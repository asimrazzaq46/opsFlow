namespace OpsFlow.Domain.Users;

/// <summary>Opaque rotating refresh token. Only its SHA-256 hash is stored; each token can be used exactly once.</summary>
public sealed class RefreshToken
{
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Concurrency token: of N concurrent rotations of the same token exactly one can persist.</summary>
    public DateTime? UsedAtUtc { get; private set; }

    public static RefreshToken Issue(Guid sessionId, string tokenHash, DateTime nowUtc, DateTime expiresAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        TokenHash = tokenHash,
        CreatedAtUtc = nowUtc,
        ExpiresAtUtc = expiresAtUtc,
    };

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;
}
