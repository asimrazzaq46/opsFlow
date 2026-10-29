using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Auth;

/// <summary>Email-verification and password-reset tokens: random, hashed at rest, expiring, single-use.</summary>
public sealed class UserTokenService(IOpsFlowDbContext db)
{
    /// <summary>Adds a new token (superseding any outstanding one of the same purpose) and returns the raw value. The caller saves.</summary>
    public async Task<string> IssueAsync(Guid userId, UserTokenPurpose purpose, TimeSpan lifetime, DateTime nowUtc, CancellationToken ct)
    {
        var outstanding = await db.UserTokens.Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAtUtc == null).ToListAsync(ct);
        outstanding.ForEach(t => t.MarkUsed(nowUtc));

        var (raw, hash) = SecureToken.Generate();
        db.UserTokens.Add(UserToken.Create(userId, purpose, hash, nowUtc, lifetime));
        return raw;
    }

    /// <summary>Finds a usable (unused, unexpired) token of this user and purpose. The caller marks it used and saves; the
    /// concurrency token on <c>UsedAtUtc</c> makes a simultaneous second use fail.</summary>
    public async Task<UserToken?> FindUsableAsync(Guid userId, UserTokenPurpose purpose, string rawToken, DateTime nowUtc, CancellationToken ct)
    {
        var hash = SecureToken.Hash(rawToken);
        var token = await db.UserTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.UserId == userId && t.Purpose == purpose, ct);
        return token is not null && token.IsUsable(nowUtc) ? token : null;
    }
}
