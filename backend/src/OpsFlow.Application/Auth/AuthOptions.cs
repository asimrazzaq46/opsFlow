namespace OpsFlow.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public JwtSettings Jwt { get; set; } = new();
    public RefreshTokenSettings RefreshToken { get; set; } = new();
    public PasswordSettings Password { get; set; } = new();
    public LockoutSettings Lockout { get; set; } = new();
    public OneTimeTokenSettings Tokens { get; set; } = new();
}

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "opsflow-api";
    public string Audience { get; set; } = "opsflow-app";

    /// <summary>HMAC signing secret (>= 32 bytes). Never stored in tracked files: user-secrets in development, secret store in production.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
}

public sealed class RefreshTokenSettings
{
    public int LifetimeDays { get; set; } = 7;

    /// <summary>Absolute cap: rotation can never extend a session beyond this.</summary>
    public int MaxSessionDays { get; set; } = 30;

    /// <summary>A just-used token presented again within this window (two tabs racing) is rejected without revoking the session.</summary>
    public int ReplayGraceSeconds { get; set; } = 10;
}

public sealed class PasswordSettings
{
    /// <summary>PBKDF2-HMAC-SHA512 iterations (OWASP minimum 210,000).</summary>
    public int Iterations { get; set; } = 210_000;
}

public sealed class LockoutSettings
{
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

public sealed class OneTimeTokenSettings
{
    public int EmailVerificationHours { get; set; } = 24;
    public int PasswordResetMinutes { get; set; } = 60;
}
