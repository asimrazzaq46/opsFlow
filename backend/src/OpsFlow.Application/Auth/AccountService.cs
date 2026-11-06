using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Auth;

/// <summary>Registration, email verification, password reset/change and account deactivation.</summary>
public sealed partial class AccountService(
    IOpsFlowDbContext db,
    IPasswordHasher hasher,
    UserTokenService tokens,
    SessionService sessions,
    IEmailService email,
    IOptions<AppOptions> app,
    IOptions<AuthOptions> auth,
    IRealtimeConnectionRevalidation connections,
    TimeProvider clock,
    ILogger<AccountService> logger
)
{
    private const int MaxAttempts = 3;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Creates a user; returns null when the email already exists (callers must not reveal that). Request handlers validate the password policy
    /// first; a violating password reaching this method is a configuration or programming defect.
    /// </summary>
    public async Task<User?> CreateAsync(
        string address,
        string displayName,
        string password,
        bool emailVerified,
        CancellationToken ct
    )
    {
        if (PasswordPolicy.Validate(password) is { Count: > 0 } violations)
        {
            throw new ArgumentException(string.Join(" ", violations), nameof(password));
        }

        var hash = hasher.Hash(password); // hashed BEFORE the duplicate check so both outcomes cost the same time
        var normalized = EmailAddress.Normalize(address);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            return null;
        }

        var user = User.Create(address, displayName, hash, emailVerified, Now);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.UserEmail))
        {
            db.ChangeTracker.Clear(); // a concurrent registration of the same email won
            return null;
        }

        return user;
    }

    public async Task RegisterAsync(
        string address,
        string displayName,
        string password,
        CancellationToken ct
    )
    {
        var user = await CreateAsync(address, displayName, password, emailVerified: false, ct);
        if (user is not null)
        {
            await SendVerificationAsync(user, ct);
        }
    }

    /// <returns>False when the link is invalid, expired or already used.</returns>
    public async Task<bool> ConfirmEmailAsync(Guid userId, string rawToken, CancellationToken ct)
    {
        var now = Now;
        var token = await tokens.FindUsableAsync(
            userId,
            UserTokenPurpose.EmailVerification,
            rawToken,
            now,
            ct
        );
        if (token is null)
        {
            return false;
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        token.MarkUsed(now);
        user.VerifyEmail(now);
        return await SaveSingleUseAsync(ct);
    }

    public async Task ResendVerificationAsync(string address, CancellationToken ct)
    {
        var user = await FindByEmailAsync(address, ct);
        if (user is { IsActive: true, IsEmailVerified: false })
        {
            await SendVerificationAsync(user, ct);
        }
    }

    public async Task ForgotPasswordAsync(string address, CancellationToken ct)
    {
        var user = await FindByEmailAsync(address, ct);
        if (user is not { IsActive: true, IsEmailVerified: true })
        {
            return;
        }

        var now = Now;
        var raw = await tokens.IssueAsync(
            user.Id,
            UserTokenPurpose.PasswordReset,
            TimeSpan.FromMinutes(auth.Value.Tokens.PasswordResetMinutes),
            now,
            ct
        );
        if (await TrySaveAsync(ct))
        {
            var link =
                $"{Base}/auth/reset-password?email={Uri.EscapeDataString(user.Email)}&token={raw}";
            email.SendDetached(
                user.Email,
                "Reset your OpsFlow password",
                $"Reset your password: {link}\n\nIf you did not request this, ignore this email."
            );
        }
    }

    /// <summary>The new password must already satisfy <see cref="PasswordPolicy"/>. False when the link is invalid, expired or already used.</summary>
    public async Task<bool> ResetPasswordAsync(
        string address,
        string rawToken,
        string newPassword,
        CancellationToken ct
    )
    {
        var now = Now;
        var user = await FindByEmailAsync(address, ct);
        if (user is null)
        {
            hasher.SimulateVerify(newPassword);
            return false;
        }

        var token = await tokens.FindUsableAsync(
            user.Id,
            UserTokenPurpose.PasswordReset,
            rawToken,
            now,
            ct
        );
        if (token is null || !user.IsActive)
        {
            return false;
        }

        token.MarkUsed(now);
        user.SetPassword(hasher.Hash(newPassword), now);
        await sessions.MarkAllRevokedAsync(user.Id, "password-reset", now, ct); // same transaction as the new password
        if (!await SaveSingleUseAsync(ct))
        {
            return false;
        }

        await connections.RequestAsync(CancellationToken.None); // committed: close the live realtime connections of the revoked sessions
        LogPasswordChanged(logger, user.Id, "reset");
        return true;
    }

    /// <summary>
    /// Changes the password and revokes EVERY session (including the caller's); the caller starts a fresh one. The new password must already
    /// satisfy <see cref="PasswordPolicy"/>. Null when the current password is wrong.
    /// </summary>
    public async Task<User?> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct
    )
    {
        var now = Now;
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(user.PasswordHash, currentPassword))
        {
            return null;
        }

        user.SetPassword(hasher.Hash(newPassword), now);
        await sessions.MarkAllRevokedAsync(user.Id, "password-changed", now, ct);
        await db.SaveChangesAsync(ct);
        await connections.RequestAsync(CancellationToken.None);
        LogPasswordChanged(logger, user.Id, "change");
        return user;
    }

    /// <summary>
    /// Deactivates the account (password required) and revokes all sessions. It takes part in the same OwnerChangeStamp
    /// protocol as membership changes: the stamps of EVERY organization the user belongs to are read first, the sole-usable-Owner
    /// check runs, then each stamp is rotated by compare-and-swap inside ONE transaction together with the deactivation. A
    /// concurrent demotion/deactivation therefore either conflicts (and this retries against fresh data) or commits first and
    /// is seen by the re-check; a rejected or conflicting attempt rolls back completely.
    /// </summary>
    /// <returns>Null when the account was deactivated; otherwise the failure to answer with.</returns>
    public async Task<ObjectResult?> DeactivateAsync(
        Guid userId,
        string password,
        CancellationToken ct
    )
    {
        var current = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(current.PasswordHash, password))
        {
            return new BadRequestObjectResult(
                new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["password"] = ["The password is incorrect."] }
                )
            );
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await using var transaction = await db.BeginTransactionAsync(ct);
            var now = Now;
            var tenantIds = await db
                .Memberships.IgnoreQueryFilters()
                .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
                .Select(m => m.TenantId)
                .ToListAsync(ct);
            var stamps = await db
                .Tenants.IgnoreQueryFilters()
                .Where(t => tenantIds.Contains(t.Id))
                .Select(t => new { t.Id, t.OwnerChangeStamp })
                .ToListAsync(ct);

            // Only USABLE Owners count (active membership + active user).
            var soleOwner = await db.UsableOwners(allTenants: true)
                .Where(m => m.UserId == userId)
                .AnyAsync(
                    m =>
                        !db.UsableOwners(allTenants: true)
                            .Any(o => o.TenantId == m.TenantId && o.UserId != userId),
                    ct
                );
            if (soleOwner)
            {
                return new ConflictObjectResult(
                    new ProblemDetails
                    {
                        Title = "You are the only active Owner of an organization.",
                        Detail = "Promote another Owner before deactivating your account.",
                    }
                );
            }

            var stale = false;
            foreach (var stamp in stamps)
            {
                var next = Guid.NewGuid();
                var rows = await db
                    .Tenants.IgnoreQueryFilters()
                    .Where(t => t.Id == stamp.Id && t.OwnerChangeStamp == stamp.OwnerChangeStamp)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.OwnerChangeStamp, next), ct);
                if (rows == 0)
                {
                    stale = true;
                    break;
                }
            }

            if (stale)
            {
                db.ChangeTracker.Clear();
                continue; // the transaction is disposed without commit: fully rolled back
            }

            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            user.Deactivate(now);
            await sessions.MarkAllRevokedAsync(userId, "account-deactivated", now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await connections.RequestAsync(CancellationToken.None);
            LogDeactivated(logger, userId);
            return null;
        }

        return new ConflictObjectResult(
            new ProblemDetails
            {
                Title = "The organization changed concurrently.",
                Detail = "Please retry.",
            }
        );
    }

    /// <returns>Null when the user does not exist.</returns>
    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct) =>
        await db
            .Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserProfileDto(
                u.Id,
                u.Email,
                u.DisplayName,
                u.PasswordHash != string.Empty,
                db.UserExternalIdentities.Any(i =>
                    i.UserId == u.Id && i.Provider == ExternalIdentityProvider.Google
                )
            ))
            .SingleOrDefaultAsync(ct);

    /// <summary>Only the display name is editable here; email, password, status and memberships have their own protected flows.</summary>
    public async Task<ActionResult<UserProfileDto>> UpdateDisplayNameAsync(
        Guid userId,
        string displayName,
        CancellationToken ct
    )
    {
        if (!DisplayNameRules.TryValidate(displayName, out var name, out var error))
        {
            return new BadRequestObjectResult(
                new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["displayName"] = [error] }
                )
            );
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.DisplayName != name)
        {
            user.SetDisplayName(name, Now);
            await db.SaveChangesAsync(ct);
            LogDisplayNameChanged(logger, userId);
        }

        var profile = await GetProfileAsync(userId, ct);
        return profile is null
            ? new NotFoundObjectResult(new ProblemDetails { Title = "User not found." })
            : profile;
    }

    private Task<User?> FindByEmailAsync(string address, CancellationToken ct)
    {
        var normalized = EmailAddress.Normalize(address);
        return db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
    }

    internal async Task SendVerificationAsync(User user, CancellationToken ct)
    {
        var raw = await tokens.IssueAsync(
            user.Id,
            UserTokenPurpose.EmailVerification,
            TimeSpan.FromHours(auth.Value.Tokens.EmailVerificationHours),
            Now,
            ct
        );
        if (await TrySaveAsync(ct))
        {
            var link = $"{Base}/auth/confirm-email?userId={user.Id}&token={raw}";
            email.SendDetached(
                user.Email,
                "Confirm your OpsFlow email",
                $"Welcome to OpsFlow. Confirm your email: {link}"
            );
        }
    }

    // A parallel request issuing a token for the same user supersedes ours; it already sent its own email.
    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <returns>False when the one-time token was consumed concurrently (nothing was persisted).</returns>
    private async Task<bool> SaveSingleUseAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private string Base => app.Value.BaseUrl.TrimEnd('/');

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} password {Operation} completed; all sessions revoked"
    )]
    private static partial void LogPasswordChanged(ILogger logger, Guid userId, string operation);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "User {UserId} deactivated; all sessions revoked"
    )]
    private static partial void LogDeactivated(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} changed their display name")]
    private static partial void LogDisplayNameChanged(ILogger logger, Guid userId);
}
