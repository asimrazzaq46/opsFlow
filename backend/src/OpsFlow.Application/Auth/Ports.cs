namespace OpsFlow.Application.Auth;

public static class AuthClaims
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string AuthVersion = "av";

    /// <summary>Distinguishes the two JWT kinds so a refresh JWT can never be used as a Bearer access token (and vice versa).</summary>
    public const string TokenType = "token_type";
    public const string AccessTokenType = "access";
    public const string RefreshTokenType = "refresh";
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

/// <summary>
/// Creates and validates the two JWT kinds (access + refresh). It only deals with tokens: hashing, rotation, replay detection and revocation
/// of refresh tokens live in <see cref="SessionService"/>. Claims are identifiers only: tenant roles are NEVER put in a token.
/// </summary>
public interface ITokenService
{
    /// <summary>Short-lived signed access JWT (sub, sid, av, token_type=access, jti).</summary>
    AccessToken CreateAccessToken(Guid userId, Guid sessionId, int authVersion, DateTime nowUtc);

    /// <summary>Signed refresh JWT (sub, sid, token_type=refresh, jti) that expires at <paramref name="expiresAtUtc"/>.</summary>
    string CreateRefreshToken(Guid userId, Guid sessionId, DateTime nowUtc, DateTime expiresAtUtc);

    /// <summary>Null unless signature, issuer, audience, lifetime, algorithm (HS256) and token type (refresh) are all valid.</summary>
    Task<RefreshTokenClaims?> ValidateRefreshTokenAsync(string token);
}

public sealed record RefreshTokenClaims(Guid UserId, Guid SessionId);

public interface ISessionValidator
{
    /// <summary>Checks on every request that the session is active, the user is active and the token's auth version is current.</summary>
    Task<bool> IsValidAsync(
        Guid userId,
        Guid sessionId,
        int authVersion,
        CancellationToken cancellationToken
    );
}

/// <summary>Claims of a Google ID token whose signature, issuer, audience, authorized party and lifetime were already verified.</summary>
public sealed record GoogleIdentity(
    string Subject,
    string? Email,
    bool EmailVerified,
    string? HostedDomain,
    string? Nonce,
    string? Name
);

public interface IGoogleIdTokenValidator
{
    /// <summary>
    /// Verifies the ID token cryptographically against Google's published keys and the configured client id. Throws an
    /// <see cref="Common.AppException"/>: 401 for an invalid token, 503 when Google's keys cannot be retrieved.
    /// </summary>
    Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary>Shared short-lived store for one-time authentication challenges. Implementations must make <see cref="TakeAsync"/> atomic.</summary>
public interface IOneTimeChallengeStore
{
    Task AddAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Returns and deletes the value in one atomic step, so a challenge can be consumed at most once; null when missing or expired.</summary>
    Task<string?> TakeAsync(string key, CancellationToken cancellationToken);
}
