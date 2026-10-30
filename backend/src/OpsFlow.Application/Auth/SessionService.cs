using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Auth;

public sealed record SessionTokens(string AccessToken, DateTime AccessTokenExpiresAtUtc, string RefreshToken, DateTime RefreshTokenExpiresAtUtc, Guid SessionId);

public sealed record SessionInfo(Guid Id, string? UserAgent, DateTime CreatedAtUtc, DateTime LastUsedAtUtc, bool IsCurrent);

/// <summary>Login, rotating refresh tokens, logout and device sessions. A session is the refresh-token family.</summary>
public sealed partial class SessionService(
    IOpsFlowDbContext db,
    IPasswordHasher hasher,
    ITokenService tokenService, // UPDATED: access + refresh JWT ek hi service se
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<SessionService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<(SessionTokens Tokens, User User)> LoginAsync(string email, string password, string? userAgent, CancellationToken ct)
    {
        var now = Now;
        var normalized = EmailAddress.Normalize(email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        // One generic failure for unknown account, lockout, wrong password, unverified and deactivated accounts.
        if (user is null || user.IsLockedOut(now))
        {
            hasher.SimulateVerify(password);
            throw InvalidCredentials();
        }

        if (!hasher.Verify(user.PasswordHash, password))
        {
            await RegisterFailureAsync(user.Id, now, ct);
            throw InvalidCredentials();
        }

        if (!user.IsActive || !user.IsEmailVerified)
        {
            throw InvalidCredentials();
        }

        user.RecordSuccessfulLogin(hasher.NeedsRehash(user.PasswordHash) ? hasher.Hash(password) : null, now);
        var tokens = Start(user, userAgent, now);
        await db.SaveChangesAsync(ct);
        LogStarted(logger, user.Id, tokens.SessionId);
        return (tokens, user);
    }

    /// <summary>Starts a brand-new session for an already authenticated user (used after a password change).</summary>
    public async Task<SessionTokens> StartAsync(User user, string? userAgent, CancellationToken ct)
    {
        var tokens = Start(user, userAgent, Now);
        await db.SaveChangesAsync(ct);
        return tokens;
    }

    /// <summary>
    /// Rotates the refresh token. The old token is marked used and the new one inserted in ONE transaction guarded by the
    /// concurrency token on <c>UsedAtUtc</c>, so of N concurrent rotations exactly one persists. A reused token later than
    /// the grace window means theft: the whole session (token family) is revoked.
    /// </summary>
    public async Task<(SessionTokens Tokens, User User)> RefreshAsync(string? rawToken, string? userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw InvalidRefresh();
        }

        // UPDATED: pehle JWT (signature/issuer/audience/expiry/algorithm/type), phir DB se hash + ownership
        var claims = await tokenService.ValidateRefreshTokenAsync(rawToken) ?? throw InvalidRefresh();
        var now = Now;
        var hash = SecureToken.Hash(rawToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw InvalidRefresh();
        var session = await db.UserSessions.SingleAsync(s => s.Id == token.SessionId, ct);
        if (session.Id != claims.SessionId || session.UserId != claims.UserId)
        {
            throw InvalidRefresh();
        }

        var user = await db.Users.SingleAsync(u => u.Id == session.UserId, ct);

        if (token.UsedAtUtc is { } usedAt)
        {
            throw await ReuseAsync(session, usedAt, now, ct);
        }

        if (!session.IsActive(now) || token.ExpiresAtUtc <= now || !user.IsActive)
        {
            throw InvalidRefresh();
        }

        token.MarkUsed(now);
        session.Touch(now);
        var expires = Earliest(now.AddDays(options.Value.RefreshToken.LifetimeDays), session.ExpiresAtUtc);
        var raw = tokenService.CreateRefreshToken(user.Id, session.Id, now, expires);
        db.RefreshTokens.Add(RefreshToken.Issue(session.Id, SecureToken.Hash(raw), now, expires));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the rotation race: another request consumed this token first (nothing of ours was persisted).
            db.ChangeTracker.Clear();
            var winnerUsedAt = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash).Select(t => t.UsedAtUtc).SingleAsync(ct);
            // UPDATED: Clear() ke baad session detached thi - revoke persist hone ke liye dobara tracked load zaroori hai
            session = await db.UserSessions.SingleAsync(s => s.Id == session.Id, ct);
            throw await ReuseAsync(session, winnerUsedAt ?? now, Now, ct); // UPDATED: fresh Now - request stall hui ho to purana timestamp galat grace deta
        }

        var access = tokenService.CreateAccessToken(user.Id, session.Id, user.AuthVersion, now);
        return (new SessionTokens(access.Token, access.ExpiresAtUtc, raw, expires, session.Id), user);
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var hash = SecureToken.Hash(rawToken);
        var session = await (from t in db.RefreshTokens
                             join s in db.UserSessions on t.SessionId equals s.Id
                             where t.TokenHash == hash
                             select s).SingleOrDefaultAsync(ct);
        if (session is not null)
        {
            session.Revoke("logout", Now);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task LogoutAllAsync(Guid userId, CancellationToken ct)
    {
        await MarkAllRevokedAsync(userId, "logout-all", Now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks every active session of the user revoked WITHOUT saving, so callers can commit it atomically with their own change.</summary>
    public async Task MarkAllRevokedAsync(Guid userId, string reason, DateTime nowUtc, CancellationToken ct)
    {
        var sessions = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null).ToListAsync(ct);
        sessions.ForEach(s => s.Revoke(reason, nowUtc));
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, Guid currentSessionId, CancellationToken ct)
    {
        var now = Now;
        var rows = await db.UserSessions.AsNoTracking().Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastUsedAtUtc).ToListAsync(ct);
        return rows.ConvertAll(s => new SessionInfo(s.Id, s.UserAgent, s.CreatedAtUtc, s.LastUsedAtUtc, s.Id == currentSessionId));
    }

    public async Task RevokeAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct)
            ?? throw AppException.NotFound("Session not found.");
        session.Revoke("revoked-by-user", Now);
        await db.SaveChangesAsync(ct);
    }

    private SessionTokens Start(User user, string? userAgent, DateTime now)
    {
        var settings = options.Value.RefreshToken;
        var session = UserSession.Start(user.Id, userAgent, now, TimeSpan.FromDays(settings.MaxSessionDays));
        var expires = Earliest(now.AddDays(settings.LifetimeDays), session.ExpiresAtUtc);
        var raw = tokenService.CreateRefreshToken(user.Id, session.Id, now, expires);
        db.UserSessions.Add(session);
        db.RefreshTokens.Add(RefreshToken.Issue(session.Id, SecureToken.Hash(raw), now, expires));

        var access = tokenService.CreateAccessToken(user.Id, session.Id, user.AuthVersion, now);
        return new SessionTokens(access.Token, access.ExpiresAtUtc, raw, expires, session.Id);
    }

    // Atomic counter (no read-modify-write) so parallel guesses cannot dodge the lockout.
    private async Task RegisterFailureAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var settings = options.Value.Lockout;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1), ct);
        var failures = await db.Users.Where(u => u.Id == userId).Select(u => u.FailedLoginCount).SingleAsync(ct);
        if (failures >= settings.MaxFailedAttempts)
        {
            DateTime? lockoutEnd = now.AddMinutes(settings.LockoutMinutes);
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.LockoutEndUtc, lockoutEnd).SetProperty(u => u.FailedLoginCount, 0), ct);
            LogLockedOut(logger, userId);
        }
    }

    private async Task<AppException> ReuseAsync(UserSession session, DateTime usedAt, DateTime now, CancellationToken ct)
    {
        if (now - usedAt <= TimeSpan.FromSeconds(options.Value.RefreshToken.ReplayGraceSeconds))
        {
            return AppException.Conflict("The refresh token was just used.", "Retry with the newest refresh token.").WithCode("AUTH_REFRESH_TOKEN_IN_USE");
        }

        session.Revoke("refresh-token-reuse", now);
        await db.SaveChangesAsync(ct);
        LogReuse(logger, session.UserId, session.Id);
        return InvalidRefresh();
    }

    private static DateTime Earliest(DateTime a, DateTime b) => a < b ? a : b;

    private static AppException InvalidCredentials() => AppException.Unauthorized("Invalid credentials or the account is not available.").WithCode("AUTH_INVALID_CREDENTIALS");

    private static AppException InvalidRefresh() => AppException.Unauthorized("The session is invalid or has expired.").WithCode("AUTH_INVALID_REFRESH_TOKEN");

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} started session {SessionId}")]
    private static partial void LogStarted(ILogger logger, Guid userId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User {UserId} locked out after repeated failed logins")]
    private static partial void LogLockedOut(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reuse detected for user {UserId}: session {SessionId} revoked")]
    private static partial void LogReuse(ILogger logger, Guid userId, Guid sessionId);
}
