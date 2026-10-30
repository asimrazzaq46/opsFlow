using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Auth;

namespace OpsFlow.Infrastructure.Security;

/// <summary>Single source of the signing key and the strict validation rules, shared by <see cref="TokenService"/> and the JWT Bearer middleware.</summary>
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
