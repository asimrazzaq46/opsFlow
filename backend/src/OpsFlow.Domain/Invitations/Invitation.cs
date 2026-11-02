using OpsFlow.Domain.Common;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Domain.Invitations;

public sealed class Invitation : ITenantOwned
{
    private Invitation()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Email { get; private set; }
    public string NormalizedEmail { get; private set; }
    public TenantRole Role { get; private set; }

    /// <summary>SHA-256 of the emailed token; the raw token is never stored.</summary>
    public string TokenHash { get; private set; }

    /// <summary>Inviting membership; a composite FK (TenantId, Id) guarantees it belongs to the same tenant.</summary>
    public Guid InvitedByMembershipId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    // Shared EmailAddress helper keeps normalization consistent with User.
    public static string Normalize(string email) => EmailAddress.Normalize(email);

    public static Invitation Create(
        Guid tenantId,
        string email,
        TenantRole role,
        string tokenHash,
        Guid invitedByMembershipId,
        DateTime nowUtc,
        TimeSpan lifetime
    )
    {
        // Customers can be invited too (customer-linked ticket workflow); only the role range is checked here.
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException("Unknown role.", nameof(role));
        }

        return new Invitation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = email.Trim(),
            NormalizedEmail = Normalize(email),
            Role = role,
            TokenHash = tokenHash,
            InvitedByMembershipId = invitedByMembershipId,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime,
        };
    }

    public bool IsOpen => AcceptedAtUtc is null && RevokedAtUtc is null;

    public bool IsPending(DateTime nowUtc) => IsOpen && ExpiresAtUtc > nowUtc;

    public void Accept(Guid userId, DateTime nowUtc)
    {
        AcceptedAtUtc = nowUtc;
        AcceptedByUserId = userId;
    }

    public void Revoke(DateTime nowUtc) => RevokedAtUtc = nowUtc;

    /// <summary>Replaces the token (every earlier link stops working) and restarts the expiry window; role and tenant never change.</summary>
    public void Reissue(string tokenHash, DateTime nowUtc, TimeSpan lifetime)
    {
        TokenHash = tokenHash;
        ExpiresAtUtc = nowUtc + lifetime;
    }
}
