using OpsFlow.Domain.Common;

namespace OpsFlow.Domain.Users;

public enum UserStatus
{
    Active,
    Deactivated,
}

/// <summary>Global user identity. Tenant roles live in memberships, never here.</summary>
public sealed class User
{
    private User()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        DisplayName = string.Empty;
        PasswordHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string NormalizedEmail { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public DateTime? EmailVerifiedAtUtc { get; private set; }
    public UserStatus Status { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }

    /// <summary>Embedded in access tokens and compared on every request; bumping it invalidates all issued access tokens.</summary>
    public int AuthVersion { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public DateTime? DeactivatedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAtUtc is not null;

    public bool IsActive => Status == UserStatus.Active;

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    public static User Create(string email, string displayName, string passwordHash, bool emailVerified, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        Email = email.Trim(),
        NormalizedEmail = EmailAddress.Normalize(email),
        DisplayName = displayName.Trim(),
        PasswordHash = passwordHash,
        EmailVerifiedAtUtc = emailVerified ? nowUtc : null,
        Status = UserStatus.Active,
        AuthVersion = 1,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    public void VerifyEmail(DateTime nowUtc)
    {
        EmailVerifiedAtUtc ??= nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void RecordSuccessfulLogin(string? upgradedPasswordHash, DateTime nowUtc)
    {
        if (upgradedPasswordHash is not null)
        {
            PasswordHash = upgradedPasswordHash;
        }

        FailedLoginCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Changes the password, clears lockout and invalidates every issued access token.</summary>
    public void SetPassword(string passwordHash, DateTime nowUtc)
    {
        PasswordHash = passwordHash;
        FailedLoginCount = 0;
        LockoutEndUtc = null;
        AuthVersion++;
        UpdatedAtUtc = nowUtc;
    }

    public void Deactivate(DateTime nowUtc)
    {
        Status = UserStatus.Deactivated;
        DeactivatedAtUtc = nowUtc;
        AuthVersion++;
        UpdatedAtUtc = nowUtc;
    }

    public void BumpAuthVersion(DateTime nowUtc)
    {
        AuthVersion++;
        UpdatedAtUtc = nowUtc;
    }
}
