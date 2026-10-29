using Microsoft.AspNetCore.Identity;

namespace OpsFlow.Infrastructure.Identity;

/// <summary>Global user identity. Tenant roles live in memberships, not here.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
