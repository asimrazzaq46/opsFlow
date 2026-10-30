using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Application.Auth;

namespace OpsFlow.Infrastructure.Security;

/// <summary>Access and refresh JWTs share one issuer/audience/HS256 key and one descriptor; only the claims and lifetime differ (token_type keeps them non-interchangeable).</summary>
internal sealed class TokenService(IOptions<AuthOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(Guid userId, Guid sessionId, int authVersion, DateTime nowUtc)
    {
        var expires = nowUtc.AddMinutes(options.Value.Jwt.AccessTokenMinutes);
        var authVersionClaim = new Claim(AuthClaims.AuthVersion, authVersion.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer32);
        return new AccessToken(CreateToken(AuthClaims.AccessTokenType, userId, sessionId, nowUtc, expires, authVersionClaim), expires);
    }

    public string CreateRefreshToken(Guid userId, Guid sessionId, DateTime nowUtc, DateTime expiresAtUtc) =>
        CreateToken(AuthClaims.RefreshTokenType, userId, sessionId, nowUtc, expiresAtUtc);

    public async Task<RefreshTokenClaims?> ValidateRefreshTokenAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, JwtTokenValidation.Parameters(options.Value.Jwt, clock));
        if (!result.IsValid || result.ClaimsIdentity is not { } identity
            || identity.FindFirst(AuthClaims.TokenType)?.Value != AuthClaims.RefreshTokenType
            || !Guid.TryParse(identity.FindFirst(AuthClaims.Subject)?.Value, out var userId)
            || !Guid.TryParse(identity.FindFirst(AuthClaims.SessionId)?.Value, out var sessionId))
        {
            return null;
        }

        return new RefreshTokenClaims(userId, sessionId);
    }

    private string CreateToken(string tokenType, Guid userId, Guid sessionId, DateTime nowUtc, DateTime expiresUtc, params Claim[] extraClaims)
    {
        var jwt = options.Value.Jwt;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expiresUtc,
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaims.Subject, userId.ToString()),
                new Claim(AuthClaims.SessionId, sessionId.ToString()),
                new Claim(AuthClaims.TokenType, tokenType),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                .. extraClaims,
            ]),
            SigningCredentials = new SigningCredentials(JwtTokenValidation.SigningKey(jwt.SigningKey), SecurityAlgorithms.HmacSha256),
        });
    }
}
