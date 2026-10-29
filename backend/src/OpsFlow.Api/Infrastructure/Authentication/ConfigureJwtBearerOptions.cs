using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;
using OpsFlow.Infrastructure.Security;

namespace OpsFlow.Api.Infrastructure.Authentication;

/// <summary>
/// JWT bearer validation. The token proves WHO and WHICH SESSION only; on every request the database is asked whether
/// that session, user and auth version are still valid, and tenant access is decided separately by the membership policies.
/// </summary>
internal sealed class ConfigureJwtBearerOptions(IOptions<AuthOptions> auth, TimeProvider clock) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

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
        options.Events = new JwtBearerEvents { OnTokenValidated = ValidateSessionAsync };
    }

    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(AuthClaims.Subject), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(AuthClaims.SessionId), out var sessionId)
            || !int.TryParse(principal.FindFirstValue(AuthClaims.AuthVersion), NumberStyles.Integer, CultureInfo.InvariantCulture, out var authVersion))
        {
            context.Fail("The access token is missing required claims.");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
        if (!await validator.IsValidAsync(userId, sessionId, authVersion, context.HttpContext.RequestAborted))
        {
            context.Fail("The session is no longer valid.");
        }
    }
}
