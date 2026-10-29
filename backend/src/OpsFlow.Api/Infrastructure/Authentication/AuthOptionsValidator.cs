using System.Text;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Auth;

namespace OpsFlow.Api.Infrastructure.Authentication;

public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}

internal sealed class AuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<AuthOptions>
{
    private const int MinProductionIterations = 210_000;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();
        var jwt = options.Jwt;
        if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
        {
            errors.Add("Auth:Jwt:SigningKey must be at least 32 bytes. Development: dotnet user-secrets set \"Auth:Jwt:SigningKey\" \"<random 48+ characters>\" --project backend/src/OpsFlow.Api. Production: supply it from the secret store.");
        }

        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            errors.Add("Auth:Jwt:Issuer and Auth:Jwt:Audience are required.");
        }

        if (jwt.AccessTokenMinutes is < 1 or > 60)
        {
            errors.Add("Auth:Jwt:AccessTokenMinutes must be between 1 and 60.");
        }

        var refresh = options.RefreshToken;
        if (refresh.LifetimeDays is < 1 or > 90 || refresh.MaxSessionDays < refresh.LifetimeDays || refresh.MaxSessionDays > 365 || refresh.ReplayGraceSeconds is < 0 or > 60)
        {
            errors.Add("Auth:RefreshToken settings are out of range.");
        }

        if (options.Password.Iterations < 1000 || (environment.IsProduction() && options.Password.Iterations < MinProductionIterations))
        {
            errors.Add($"Auth:Password:Iterations is too low (production minimum {MinProductionIterations}).");
        }

        if (options.Lockout.MaxFailedAttempts is < 3 or > 20 || options.Lockout.LockoutMinutes is < 1 or > 1440)
        {
            errors.Add("Auth:Lockout settings are out of range.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class CorsSettingsValidator(IHostEnvironment environment) : IValidateOptions<CorsSettings>
{
    public ValidateOptionsResult Validate(string? name, CorsSettings options) =>
        !environment.IsProduction() || (options.AllowedOrigins.Length > 0 && options.AllowedOrigins.All(o => o.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !o.Contains('*', StringComparison.Ordinal)))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Cors:AllowedOrigins must list explicit https origins in production (no wildcards).");
}
