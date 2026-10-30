using System.Security.Cryptography;
using System.Text;

namespace OpsFlow.Application.Common;

/// <summary>Opaque random secrets (invitation, email-verification, reset and refresh tokens). Only the SHA-256 hash is ever stored.</summary>
public static class SecureToken
{
    /// <summary>Returns a URL-safe 256-bit random token (handed out once) and the hash that is persisted.</summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
