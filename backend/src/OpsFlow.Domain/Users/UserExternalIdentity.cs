namespace OpsFlow.Domain.Users;

public enum ExternalIdentityProvider
{
    Google,
}

/// <summary>
/// Maps a verified external account to a global OpsFlow user. The provider's stable subject is the identity key; the email is never used
/// for matching. No provider tokens are stored.
/// </summary>
public sealed class UserExternalIdentity
{
    public const int MaxSubjectLength = 255;

    private UserExternalIdentity()
    {
        ProviderSubject = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public ExternalIdentityProvider Provider { get; private set; }
    public string ProviderSubject { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }

    public static UserExternalIdentity Create(
        Guid userId,
        ExternalIdentityProvider provider,
        string providerSubject,
        DateTime nowUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = provider,
            ProviderSubject = providerSubject,
            CreatedAtUtc = nowUtc,
        };

    public void RecordLogin(DateTime nowUtc) => LastLoginAtUtc = nowUtc;
}
