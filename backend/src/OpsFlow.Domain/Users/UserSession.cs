namespace OpsFlow.Domain.Users;

/// <summary>One signed-in device. It is also the refresh-token family: revoking it kills every token in the family.</summary>
public sealed class UserSession
{
    private UserSession() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime LastUsedAtUtc { get; private set; }

    /// <summary>Absolute lifetime; rotation never extends a session beyond it.</summary>
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }

    public static UserSession Start(
        Guid userId,
        string? userAgent,
        DateTime nowUtc,
        TimeSpan maxLifetime
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
            CreatedAtUtc = nowUtc,
            LastUsedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + maxLifetime,
        };

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Touch(DateTime nowUtc) => LastUsedAtUtc = nowUtc;

    public void Revoke(string reason, DateTime nowUtc)
    {
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevocationReason = reason;
    }
}
