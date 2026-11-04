using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;

namespace OpsFlow.Infrastructure.Security;

/// <summary>
/// Verifies Google ID tokens with <see cref="GoogleJsonWebSignature"/>: RS256 signature against Google's published keys (cached and refreshed
/// by the library), issuer, audience (the configured client id), expiry and issued-at. The authorized party is checked additionally.
/// Raw tokens are never logged.
/// </summary>
internal sealed partial class GoogleIdTokenValidator(
    IOptions<GoogleAuthOptions> options,
    ILogger<GoogleIdTokenValidator> logger
) : IGoogleIdTokenValidator
{
    private const int MaxTokenLength = 8192;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    public async Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > MaxTokenLength)
        {
            return null;
        }

        var clientId = options.Value.ClientId;
        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature
                .ValidateAsync(
                    idToken,
                    new GoogleJsonWebSignature.ValidationSettings
                    {
                        Audience = [clientId],
                        IssuedAtClockTolerance = ClockTolerance,
                        ExpirationTimeClockTolerance = ClockTolerance,
                    }
                )
                .WaitAsync(Timeout, cancellationToken);
        }
        catch (InvalidJwtException ex)
        {
            LogRejected(logger, ex.Message);
            return null;
        }
        catch (Exception ex)
            when ((ex is HttpRequestException or TimeoutException or TaskCanceledException)
                && !cancellationToken.IsCancellationRequested)
        {
            // Google's keys could not be retrieved: a temporary infrastructure failure, not an invalid credential.
            LogUnavailable(logger, ex.GetType().Name);
            throw new AppException(
                503,
                "Google sign-in is temporarily unavailable.",
                "Please try again shortly."
            ).WithCode("AUTH_GOOGLE_UNAVAILABLE");
        }

        // The token is signature-verified at this point; when azp is present it must be this client.
        if (
            new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(idToken).TryGetPayloadValue<string>("azp", out var azp)
            && !string.Equals(azp, clientId, StringComparison.Ordinal)
        )
        {
            LogRejected(logger, "authorized party mismatch");
            return null;
        }

        return new GoogleIdentity(
            payload.Subject,
            payload.Email,
            payload.EmailVerified,
            payload.HostedDomain,
            payload.Nonce,
            payload.Name
        );
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Google ID token rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Google ID token validation unavailable ({ErrorType})")]
    private static partial void LogUnavailable(ILogger logger, string errorType);
}
