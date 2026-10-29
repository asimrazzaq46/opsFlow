namespace OpsFlow.Application.Auth;

public static class AuthClaims
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string AuthVersion = "av";
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);

    /// <summary>True when the stored hash uses weaker parameters than currently configured.</summary>
    bool NeedsRehash(string hash);

    /// <summary>Burns the same CPU time as a real verification (unknown account) so login timing does not reveal existence.</summary>
    void SimulateVerify(string password);
}

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

public interface IAccessTokenService
{
    /// <summary>Issues a short-lived signed JWT carrying only user, session and auth-version identifiers (no roles, no tenants).</summary>
    AccessToken Issue(Guid userId, Guid sessionId, int authVersion, DateTime nowUtc);
}

public interface ISessionValidator
{
    /// <summary>Checks on every request that the session is active, the user is active and the token's auth version is current.</summary>
    Task<bool> IsValidAsync(Guid userId, Guid sessionId, int authVersion, CancellationToken cancellationToken);
}
