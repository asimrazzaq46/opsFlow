using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>
/// Email and phone are accepted only to detect an attempted change: they must be omitted or equal to the stored values, because
/// only the account owner may change their own contact details.
/// </summary>
public sealed record CustomerUpdateRequest(
    [Required, StringLength(150, MinimumLength = 2)] string Name,
    [EmailAddress, StringLength(256)] string? Email,
    [StringLength(50)] string? Phone,
    Guid? LinkedUserId
);
