using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Auth;

public enum GoogleChallengePurpose
{
    SignIn,
    Link,
}

/// <summary>One-time challenge for the Google Identity Services button. The nonce goes into the ID token; the id is sent back with it.</summary>
public sealed record GoogleChallenge(
    string ChallengeId,
    string Nonce,
    DateTime ExpiresAtUtc,
    string ClientId
);

/// <summary>Tokens are null when a new account must confirm its email address first.</summary>
public sealed record GoogleSignInResult(SessionTokens? Tokens, User User);

/// <summary>
/// Google sign-in, registration and account linking. Google only proves the external identity (stable <c>sub</c>); account state,
/// memberships, roles, JWTs and refresh-token sessions stay with OpsFlow (<see cref="SessionService"/>). Accounts are never matched or
/// linked by email; no tenant membership or role is ever granted here.
/// </summary>
public sealed partial class GoogleAuthService(
    IOpsFlowDbContext db,
    IGoogleIdTokenValidator validator,
    IOneTimeChallengeStore challenges,
    SessionService sessions,
    AccountService accounts,
    IOptions<GoogleAuthOptions> options,
    TimeProvider clock,
    ILogger<GoogleAuthService> logger
)
{
    private const ExternalIdentityProvider Google = ExternalIdentityProvider.Google;
    private const int MaxChallengeIdLength = 128;
    private const int MaxEmailLength = 256;

    private sealed record StoredChallenge(GoogleChallengePurpose Purpose, Guid? UserId, string NonceHash);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ActionResult<GoogleChallenge>> CreateSignInChallengeAsync(CancellationToken ct) =>
        Disabled() is { } off ? off : await IssueAsync(GoogleChallengePurpose.SignIn, null, ct);

    /// <summary>Linking requires a password re-authentication now; the challenge is then bound to this user and usable for linking only.</summary>
    public async Task<ActionResult<GoogleChallenge>> CreateLinkChallengeAsync(
        Guid userId,
        string password,
        CancellationToken ct
    )
    {
        if (Disabled() is { } off)
        {
            return off;
        }

        if (await sessions.VerifyPasswordAsync(userId, password, ct) is { } failure)
        {
            return failure;
        }

        return await IssueAsync(GoogleChallengePurpose.Link, userId, ct);
    }

    public async Task<ActionResult<GoogleSignInResult>> SignInAsync(
        string credential,
        string challengeId,
        string? userAgent,
        CancellationToken ct
    )
    {
        var verified = await VerifyAsync(credential, challengeId, GoogleChallengePurpose.SignIn, null, ct);
        if (verified.Result is ObjectResult rejected)
        {
            return rejected;
        }

        var identity = verified.Value!;

        // Second pass only after losing a concurrent first-sign-in race for the same subject: the winner's link then exists.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var linkedUserId = await db
                .UserExternalIdentities.AsNoTracking()
                .Where(i => i.Provider == Google && i.ProviderSubject == identity.Subject)
                .Select(i => (Guid?)i.UserId)
                .SingleOrDefaultAsync(ct);
            if (linkedUserId is { } userId)
            {
                return await SignInLinkedAsync(userId, identity.Subject, userAgent, ct);
            }

            var attemptResult = await TryRegisterAsync(identity, userAgent, ct);
            if (attemptResult.Result is ObjectResult failure)
            {
                return failure;
            }

            if (attemptResult.Value is { } registered)
            {
                return registered;
            }
        }

        return new ConflictObjectResult(
            new ProblemDetails { Title = "The sign-in changed concurrently.", Detail = "Please retry." }
        );
    }

    /// <returns>Null when linked (or already linked to this user); otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> LinkAsync(Guid userId, string credential, string challengeId, CancellationToken ct)
    {
        var verified = await VerifyAsync(credential, challengeId, GoogleChallengePurpose.Link, userId, ct);
        if (verified.Result is ObjectResult rejected)
        {
            return rejected;
        }

        var identity = verified.Value!;
        var existing = await db
            .UserExternalIdentities.AsNoTracking()
            .Where(i => i.Provider == Google && (i.ProviderSubject == identity.Subject || i.UserId == userId))
            .Select(i => new { i.UserId, i.ProviderSubject })
            .ToListAsync(ct);
        if (existing.Exists(e => e.ProviderSubject == identity.Subject))
        {
            if (existing.Exists(e => e.ProviderSubject == identity.Subject && e.UserId == userId))
            {
                return null; // already linked to this user: idempotent
            }

            return IdentityConflict();
        }

        if (existing.Count > 0)
        {
            return AlreadyLinked();
        }

        db.UserExternalIdentities.Add(UserExternalIdentity.Create(userId, Google, identity.Subject, Now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ExternalIdentitySubject))
        {
            db.ChangeTracker.Clear();
            var owner = await db
                .UserExternalIdentities.AsNoTracking()
                .Where(i => i.Provider == Google && i.ProviderSubject == identity.Subject)
                .Select(i => (Guid?)i.UserId)
                .SingleOrDefaultAsync(ct);
            return owner == userId
                ? null // the same user won a concurrent duplicate request
                : IdentityConflict();
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ExternalIdentityUser))
        {
            db.ChangeTracker.Clear();
            return AlreadyLinked();
        }

        LogLinked(logger, userId);
        return null;
    }

    private async Task<GoogleChallenge> IssueAsync(
        GoogleChallengePurpose purpose,
        Guid? userId,
        CancellationToken ct
    )
    {
        var (challengeId, _) = SecureToken.Generate();
        var (nonce, nonceHash) = SecureToken.Generate();
        var lifetime = TimeSpan.FromSeconds(options.Value.ChallengeLifetimeSeconds);
        await challenges.AddAsync(
            Key(challengeId),
            JsonSerializer.Serialize(new StoredChallenge(purpose, userId, nonceHash)),
            lifetime,
            ct
        );
        return new GoogleChallenge(challengeId, nonce, Now + lifetime, options.Value.ClientId);
    }

    /// <summary>
    /// Consumes the challenge FIRST (atomic, so a replay or a parallel duplicate fails even before Google is contacted), then verifies the
    /// token and binds it to the challenge through the nonce. No database transaction is open during the network call.
    /// </summary>
    private async Task<ActionResult<GoogleIdentity>> VerifyAsync(
        string credential,
        string challengeId,
        GoogleChallengePurpose purpose,
        Guid? userId,
        CancellationToken ct
    )
    {
        if (Disabled() is { } off)
        {
            return off;
        }

        if (string.IsNullOrWhiteSpace(challengeId) || challengeId.Length > MaxChallengeIdLength)
        {
            return ChallengeInvalid();
        }

        var stored = await challenges.TakeAsync(Key(challengeId), ct) is { } json
            ? JsonSerializer.Deserialize<StoredChallenge>(json)
            : null;
        if (stored is null || stored.Purpose != purpose || stored.UserId != userId)
        {
            return ChallengeInvalid();
        }

        if (await validator.ValidateAsync(credential, ct) is not { } identity)
        {
            return TokenInvalid();
        }

        if (
            identity.Nonce is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(SecureToken.Hash(identity.Nonce)),
                Encoding.ASCII.GetBytes(stored.NonceHash)
            )
        )
        {
            return ChallengeInvalid();
        }

        if (string.IsNullOrEmpty(identity.Subject) || identity.Subject.Length > UserExternalIdentity.MaxSubjectLength)
        {
            return TokenInvalid();
        }

        if (
            !identity.EmailVerified
            || string.IsNullOrWhiteSpace(identity.Email)
            || identity.Email.Length > MaxEmailLength
            || !identity.Email.Contains('@', StringComparison.Ordinal)
        )
        {
            return Fail(
                StatusCodes.Status403Forbidden,
                "AUTH_GOOGLE_EMAIL_NOT_VERIFIED",
                "The Google account has no verified email address."
            );
        }

        return identity;
    }

    private async Task<ActionResult<GoogleSignInResult>> SignInLinkedAsync(
        Guid userId,
        string subject,
        string? userAgent,
        CancellationToken ct
    )
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!user.IsActive)
        {
            return Fail(StatusCodes.Status403Forbidden, "AUTH_ACCOUNT_DISABLED", "This account is deactivated.");
        }

        if (!user.IsEmailVerified)
        {
            return Fail(
                StatusCodes.Status403Forbidden,
                "AUTH_EMAIL_NOT_VERIFIED",
                "Confirm your email address first.",
                "Use the link in the confirmation email."
            );
        }

        var link = await db.UserExternalIdentities.SingleAsync(
            i => i.Provider == Google && i.ProviderSubject == subject,
            ct
        );
        var now = Now;
        link.RecordLogin(now);
        user.RecordExternalLogin(now);
        var tokens = await sessions.StartAsync(user, userAgent, ct); // one SaveChanges: login markers + new session
        LogSignedIn(logger, user.Id, tokens.SessionId);
        return new GoogleSignInResult(tokens, user);
    }

    /// <returns>The failure (Result), the registration, or a null Value when a concurrent request registered the same Google subject first (the caller then signs in through that link).</returns>
    private async Task<ActionResult<GoogleSignInResult?>> TryRegisterAsync(
        GoogleIdentity identity,
        string? userAgent,
        CancellationToken ct
    )
    {
        var email = identity.Email!;
        var normalized = EmailAddress.Normalize(email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            return LinkRequired(); // never sign in to, or link, an existing account by email equality
        }

        var now = Now;
        // Email ownership is accepted from Google only where Google is authoritative for the address; otherwise OpsFlow verifies it.
        var verified = IsGoogleAuthoritative(identity);
        var user = User.Create(email, DisplayNameRules.FromExternal(identity.Name, email), string.Empty, verified, now);
        var link = UserExternalIdentity.Create(user.Id, Google, identity.Subject, now);
        db.Users.Add(user);
        db.UserExternalIdentities.Add(link);
        try
        {
            if (verified)
            {
                link.RecordLogin(now);
                user.RecordExternalLogin(now);
                var tokens = await sessions.StartAsync(user, userAgent, ct); // user, link and session in ONE SaveChanges
                LogRegistered(logger, user.Id);
                return new GoogleSignInResult(tokens, user);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ExternalIdentitySubject))
        {
            db.ChangeTracker.Clear();
            return default(GoogleSignInResult);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.UserEmail))
        {
            db.ChangeTracker.Clear();
            return LinkRequired();
        }

        LogRegistered(logger, user.Id);
        await accounts.SendVerificationAsync(user, ct);
        return new GoogleSignInResult(null, user);
    }

    // Google documents that gmail.com addresses and Google Workspace accounts (hd claim matching the domain) are authoritatively verified.
    private static bool IsGoogleAuthoritative(GoogleIdentity identity)
    {
        var email = identity.Email!;
        var domain = email[(email.LastIndexOf('@') + 1)..];
        return domain.Equals("gmail.com", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("googlemail.com", StringComparison.OrdinalIgnoreCase)
            || (
                !string.IsNullOrEmpty(identity.HostedDomain)
                && domain.Equals(identity.HostedDomain, StringComparison.OrdinalIgnoreCase)
            );
    }

    private ObjectResult? Disabled() =>
        options.Value.Enabled
            ? null
            : Fail(StatusCodes.Status404NotFound, "AUTH_GOOGLE_DISABLED", "Google sign-in is not enabled.");

    // Only the hash of the challenge id is used as the key, so the store never holds a usable reference.
    private static string Key(string challengeId) => $"google:{SecureToken.Hash(challengeId)}";

    /// <summary>A failure carrying a stable <c>code</c>, which the API copies into the ProblemDetails body.</summary>
    private static ObjectResult Fail(int status, string code, string title, string? detail = null) =>
        new(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = status,
            Extensions = { ["code"] = code },
        })
        {
            StatusCode = status,
        };

    private static ObjectResult ChallengeInvalid() =>
        Fail(
            StatusCodes.Status400BadRequest,
            "AUTH_GOOGLE_CHALLENGE_INVALID",
            "The Google sign-in request is invalid or has expired.",
            "Start the Google sign-in again."
        );

    private static ObjectResult TokenInvalid() =>
        Fail(StatusCodes.Status401Unauthorized, "AUTH_GOOGLE_TOKEN_INVALID", "The Google credential is invalid.");

    private static ObjectResult LinkRequired() =>
        Fail(
            StatusCodes.Status409Conflict,
            "AUTH_GOOGLE_LINK_REQUIRED",
            "An OpsFlow account already uses this email address.",
            "Sign in with your password, then link Google from your account settings."
        );

    private static ObjectResult IdentityConflict() =>
        Fail(
            StatusCodes.Status409Conflict,
            "AUTH_GOOGLE_IDENTITY_CONFLICT",
            "This Google account is linked to a different OpsFlow account."
        );

    private static ObjectResult AlreadyLinked() =>
        Fail(
            StatusCodes.Status409Conflict,
            "AUTH_GOOGLE_ALREADY_LINKED",
            "Your account is already linked to another Google account."
        );

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with Google (session {SessionId})")]
    private static partial void LogSignedIn(ILogger logger, Guid userId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} registered with Google")]
    private static partial void LogRegistered(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} linked a Google account")]
    private static partial void LogLinked(ILogger logger, Guid userId);
}
