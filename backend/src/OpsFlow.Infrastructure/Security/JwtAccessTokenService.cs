using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Auth;

namespace OpsFlow.Infrastructure.Security;

/// <summary>Signs short-lived HS256 access tokens. Claims are identifiers only: tenant roles are NEVER put in the token.</summary>
internal sealed class JwtAccessTokenService(IOptions<AuthOptions> options) : IAccessTokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(Guid userId, Guid sessionId, int authVersion, DateTime nowUtc)
    {
        var jwt = options.Value.Jwt;
        var expires = nowUtc.AddMinutes(jwt.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expires,
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaims.Subject, userId.ToString()),
                new Claim(AuthClaims.SessionId, sessionId.ToString()),
                new Claim(AuthClaims.AuthVersion, authVersion.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer32),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(JwtTokenValidation.SigningKey(jwt.SigningKey), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }
}

public static class JwtTokenValidation
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    public static SymmetricSecurityKey SigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    /// <summary>Strict validation: signature, issuer, audience, lifetime and ONLY HS256 are accepted.</summary>
    public static TokenValidationParameters Parameters(JwtSettings jwt, TimeProvider clock) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(jwt.SigningKey),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        // The injected clock (not the system clock) decides expiry, so lifetime behaviour is testable.
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return expires is not null && expires.Value.ToUniversalTime() + Skew > now
                && (notBefore is null || notBefore.Value.ToUniversalTime() - Skew <= now);
        },
        NameClaimType = AuthClaims.Subject,
    };
}
