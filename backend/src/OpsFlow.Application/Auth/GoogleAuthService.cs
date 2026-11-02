using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public Task<GoogleChallenge> CreateSignInChallengeAsync(CancellationToken ct) =>
        IssueAsync(GoogleChallengePurpose.SignIn, null, ct);

    /// <summary>Linking requires a password re-authentication now; the challenge is then bound to this user and usable for linking only.</summary>
    public async Task<GoogleChallenge> CreateLinkChallengeAsync(
        Guid userId,
        string password,
        CancellationToken ct
    )
    {
        EnsureEnabled();
        await sessions.VerifyPasswordAsync(userId, password, ct);
        return await IssueAsync(GoogleChallengePurpose.Link, userId, ct);
    }

    public async Task<GoogleSignInResult> SignInAsync(
        string credential,
        string challengeId,
        string? userAgent,
        CancellationToken ct
    )
    {
        var identity = await VerifyAsync(credential, challengeId, GoogleChallengePurpose.SignIn, null, ct);

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

            if (await TryRegisterAsync(identity, userAgent, ct) is { } registered)
            {
                return registered;
            }
        }

        throw AppException.Conflict("The sign-in changed concurrently.", "Please retry.");
    }

    public async Task LinkAsync(Guid userId, string credential, string challengeId, CancellationToken ct)
    {
        var identity = await VerifyAsync(credential, challengeId, GoogleChallengePurpose.Link, userId, ct);
        var existing = await db
            .UserExternalIdentities.AsNoTracking()
            .Where(i => i.Provider == Google && (i.ProviderSubject == identity.Subject || i.UserId == userId))
            .Select(i => new { i.UserId, i.ProviderSubject })
            .ToListAsync(ct);
        if (existing.Exists(e => e.ProviderSubject == identity.Subject))
        {
            if (existing.Exists(e => e.ProviderSubject == identity.Subject && e.UserId == userId))
            {
                return; // already linked to this user: idempotent
            }

            throw IdentityConflict();
        }

        if (existing.Count > 0)
        {
            throw AlreadyLinked();
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
            if (owner == userId)
            {
                return; // the same user won a concurrent duplicate request
            }

            throw IdentityConflict();
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.ExternalIdentityUser))
        {
            db.ChangeTracker.Clear();
            throw AlreadyLinked();
        }

        LogLinked(logger, userId);
    }

    private async Task<GoogleChallenge> IssueAsync(
        GoogleChallengePurpose purpose,
        Guid? userId,
        CancellationToken ct
    )
    {
        EnsureEnabled();
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
    private async Task<GoogleIdentity> VerifyAsync(
        string credential,
        string challengeId,
        GoogleChallengePurpose purpose,
        Guid? userId,
        CancellationToken ct
    )
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(challengeId) || challengeId.Length > MaxChallengeIdLength)
        {
            throw ChallengeInvalid();
        }

        var stored = await challenges.TakeAsync(Key(challengeId), ct) is { } json
            ? JsonSerializer.Deserialize<StoredChallenge>(json)
            : null;
        if (stored is null || stored.Purpose != purpose || stored.UserId != userId)
        {
            throw ChallengeInvalid();
        }

        var identity = await validator.ValidateAsync(credential, ct);
        if (
            identity.Nonce is null
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(SecureToken.Hash(identity.Nonce)),
                Encoding.ASCII.GetBytes(stored.NonceHash)
            )
        )
        {
            throw ChallengeInvalid();
        }

        if (string.IsNullOrEmpty(identity.Subject) || identity.Subject.Length > UserExternalIdentity.MaxSubjectLength)
        {
            throw TokenInvalid();
        }

        if (
            !identity.EmailVerified
            || string.IsNullOrWhiteSpace(identity.Email)
            || identity.Email.Length > MaxEmailLength
            || !identity.Email.Contains('@', StringComparison.Ordinal)
        )
        {
            throw AppException
                .Forbidden("The Google account has no verified email address.")
                .WithCode("AUTH_GOOGLE_EMAIL_NOT_VERIFIED");
        }

        return identity;
    }

    private async Task<GoogleSignInResult> SignInLinkedAsync(
        Guid userId,
        string subject,
        string? userAgent,
        CancellationToken ct
    )
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!user.IsActive)
        {
            throw AppException.Forbidden("This account is deactivated.").WithCode("AUTH_ACCOUNT_DISABLED");
        }

        if (!user.IsEmailVerified)
        {
            throw AppException
                .Forbidden("Confirm your email address first.", "Use the link in the confirmation email.")
                .WithCode("AUTH_EMAIL_NOT_VERIFIED");
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

    /// <summary>Null when a concurrent request registered the same Google subject first (the caller then signs in through that link).</summary>
    private async Task<GoogleSignInResult?> TryRegisterAsync(
        GoogleIdentity identity,
        string? userAgent,
        CancellationToken ct
    )
    {
        var email = identity.Email!;
        var normalized = EmailAddress.Normalize(email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            throw LinkRequired(); // never sign in to, or link, an existing account by email equality
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
            return null;
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.UserEmail))
        {
            db.ChangeTracker.Clear();
            throw LinkRequired();
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

    private void EnsureEnabled()
    {
        if (!options.Value.Enabled)
        {
            throw AppException.NotFound("Google sign-in is not enabled.").WithCode("AUTH_GOOGLE_DISABLED");
        }
    }

    // Only the hash of the challenge id is used as the key, so the store never holds a usable reference.
    private static string Key(string challengeId) => $"google:{SecureToken.Hash(challengeId)}";

    private static AppException ChallengeInvalid() =>
        AppException
            .BadRequest("The Google sign-in request is invalid or has expired.", "Start the Google sign-in again.")
            .WithCode("AUTH_GOOGLE_CHALLENGE_INVALID");

    private static AppException TokenInvalid() =>
        AppException.Unauthorized("The Google credential is invalid.").WithCode("AUTH_GOOGLE_TOKEN_INVALID");

    private static AppException LinkRequired() =>
        AppException
            .Conflict(
                "An OpsFlow account already uses this email address.",
                "Sign in with your password, then link Google from your account settings."
            )
            .WithCode("AUTH_GOOGLE_LINK_REQUIRED");

    private static AppException IdentityConflict() =>
        AppException
            .Conflict("This Google account is linked to a different OpsFlow account.")
            .WithCode("AUTH_GOOGLE_IDENTITY_CONFLICT");

    private static AppException AlreadyLinked() =>
        AppException
            .Conflict("Your account is already linked to another Google account.")
            .WithCode("AUTH_GOOGLE_ALREADY_LINKED");

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in with Google (session {SessionId})")]
    private static partial void LogSignedIn(ILogger logger, Guid userId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} registered with Google")]
    private static partial void LogRegistered(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} linked a Google account")]
    private static partial void LogLinked(ILogger logger, Guid userId);
}
