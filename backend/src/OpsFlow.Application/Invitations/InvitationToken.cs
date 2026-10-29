using System.Security.Cryptography;
using System.Text;

namespace OpsFlow.Application.Invitations;

public static class InvitationToken
{
    /// <summary>Returns a URL-safe random token (emailed once) and the SHA-256 hash that is persisted.</summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
