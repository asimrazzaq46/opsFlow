namespace OpsFlow.Domain.Users;

/// <summary>Single-use, expiring token (email verification, password reset). Only its SHA-256 hash is stored.</summary>
public sealed class UserToken
{
    private UserToken()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public UserTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Concurrency token: two concurrent uses of the same token cannot both succeed.</summary>
    public DateTime? UsedAtUtc { get; private set; }

    public static UserToken Create(
        Guid userId,
        UserTokenPurpose purpose,
        string tokenHash,
        DateTime nowUtc,
        TimeSpan lifetime
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime,
        };

    public bool IsUsable(DateTime nowUtc) => UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc = nowUtc;
}
