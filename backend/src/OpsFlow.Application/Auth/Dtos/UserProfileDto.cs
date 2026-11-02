namespace OpsFlow.Application.Auth;

/// <summary>The authenticated user's own profile. <c>HasPassword</c> and <c>GoogleLinked</c> describe the available sign-in methods only.</summary>
public sealed record UserProfileDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool HasPassword,
    bool GoogleLinked
);
