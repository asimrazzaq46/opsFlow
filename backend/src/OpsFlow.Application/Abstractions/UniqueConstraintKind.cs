namespace OpsFlow.Application.Abstractions;

/// <summary>Unique constraints whose violation has a specific, expected meaning for a use case.</summary>
public enum UniqueConstraintKind
{
    UserEmail,
    TenantSlug,
    MembershipUser,
    OpenInvitation,
}
