namespace OpsFlow.Domain.Common;

public static class EmailAddress
{
    /// <summary>Canonical form used for uniqueness and lookups.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
