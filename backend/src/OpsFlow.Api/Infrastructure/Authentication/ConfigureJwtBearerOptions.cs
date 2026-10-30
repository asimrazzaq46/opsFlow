using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Api.Infrastructure.Errors;
using OpsFlow.Application.Auth;
using OpsFlow.Infrastructure.Security;
using OpsFlow.Realtime;

namespace OpsFlow.Api.Infrastructure.Authentication;

/// <summary>
/// JWT bearer validation. The token proves WHO and WHICH SESSION only; on every request the database is asked whether
/// that session, user and auth version are still valid, and tenant access is decided separately by the membership policies.
/// </summary>
internal sealed class ConfigureJwtBearerOptions(
    IOptions<AuthOptions> auth,
    IOptions<RealtimeOptions> realtime,
    TimeProvider clock
) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) =>
        Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.MapInboundClaims = false; // keep "sub"/"sid"/"av" as issued
        options.RequireHttpsMetadata = false; // no metadata endpoint is used; the key is local
        options.SaveToken = false;
        options.TokenValidationParameters = JwtTokenValidation.Parameters(auth.Value.Jwt, clock);
        // UPDATED: SignalR WebSocket header nahi bhej sakta - sirf hub path par query access_token qubool; baqi validation bilkul wahi (access type + DB session)
        var hubPath = realtime.Value.HubPath;
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (
                    string.IsNullOrEmpty(context.Token)
                    && context.HttpContext.HubAccessToken(hubPath) is { } token
                )
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = ValidateSessionAsync,
            OnChallenge = RecordChallengeCode,
        };
    }

    // UPDATED: 401 body (StatusCodePages) mein stable code: token missing / expired / invalid. Default challenge (WWW-Authenticate) barqarar.
    private static Task RecordChallengeCode(JwtBearerChallengeContext context)
    {
        context.HttpContext.Items[ProblemDetailsSetup.CodeItem] = context.AuthenticateFailure switch
        {
            null => "AUTH_TOKEN_MISSING",
            SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException =>
                "AUTH_TOKEN_EXPIRED", // custom LifetimeValidator => InvalidLifetime
            _ => "AUTH_TOKEN_INVALID",
        };
        return Task.CompletedTask;
    }

    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        // UPDATED: refresh JWT (ya token_type ke baghair token) Bearer access token ke taur par reject
        if (principal?.FindFirstValue(AuthClaims.TokenType) != AuthClaims.AccessTokenType)
        {
            context.Fail("Only access tokens can authenticate API requests.");
            return;
        }

        if (
            !Guid.TryParse(principal.FindFirstValue(AuthClaims.Subject), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(AuthClaims.SessionId), out var sessionId)
            || !int.TryParse(
                principal.FindFirstValue(AuthClaims.AuthVersion),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var authVersion
            )
        )
        {
            context.Fail("The access token is missing required claims.");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
        if (
            !await validator.IsValidAsync(
                userId,
                sessionId,
                authVersion,
                context.HttpContext.RequestAborted
            )
        )
        {
            context.Fail("The session is no longer valid.");
        }
    }
}
