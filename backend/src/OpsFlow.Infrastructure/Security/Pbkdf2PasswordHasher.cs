using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;

namespace OpsFlow.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA512 (built-in .NET primitive) with a random 128-bit salt per password and a self-describing format
/// <c>v1.pbkdf2-sha512.{iterations}.{salt}.{hash}</c>, so the cost can be raised later and old hashes are upgraded on login.
/// </summary>
internal sealed class Pbkdf2PasswordHasher(IOptions<AuthOptions> options) : IPasswordHasher
{
    private const string Prefix = "v1.pbkdf2-sha512";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MaxAcceptedIterations = 5_000_000; // a corrupted/hostile stored value must not become a CPU DoS

    private readonly Lazy<string> _dummyHash = new(() => CreateHash("dummy-password-for-timing", options.Value.Password.Iterations));

    public string Hash(string password) => CreateHash(password, options.Value.Password.Iterations);

    public bool Verify(string hash, string password)
    {
        var parts = hash.Split('.');
        if (parts.Length != 5 || $"{parts[0]}.{parts[1]}" != Prefix
            || !int.TryParse(parts[2], out var iterations) || iterations is < 1 or > MaxAcceptedIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool NeedsRehash(string hash) =>
        !int.TryParse(hash.Split('.').ElementAtOrDefault(2), out var iterations) || iterations < options.Value.Password.Iterations;

    public void SimulateVerify(string password) => Verify(_dummyHash.Value, password);

    private static string CreateHash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
        return $"{Prefix}.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
