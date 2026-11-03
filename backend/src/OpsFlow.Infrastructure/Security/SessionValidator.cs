using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Auth;
using OpsFlow.Domain.Users;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Security;

/// <summary>One indexed query per request: the JWT is only a pointer, the database decides whether it is still honoured.</summary>
internal sealed class SessionValidator(OpsFlowDbContext db, TimeProvider clock) : ISessionValidator
{
    public Task<bool> IsValidAsync(
        Guid userId,
        Guid sessionId,
        int authVersion,
        CancellationToken cancellationToken
    ) =>
        ValidSessions(db, clock.GetUtcNow().UtcDateTime)
            .AnyAsync(
                v => v.SessionId == sessionId && v.UserId == userId && v.AuthVersion == authVersion,
                cancellationToken
            );

    /// <summary>
    /// The single definition of "this session may authenticate": not revoked, not expired and owned by an active user. A token
    /// additionally has to carry the user's CURRENT auth version. Per-request validation and live-connection revalidation both use it.
    /// </summary>
    internal static IQueryable<ValidSession> ValidSessions(OpsFlowDbContext db, DateTime nowUtc) =>
        from s in db.UserSessions.AsNoTracking()
        where s.RevokedAtUtc == null && s.ExpiresAtUtc > nowUtc
        join u in db.Users on s.UserId equals u.Id
        where u.Status == UserStatus.Active
        select new ValidSession
        {
            SessionId = s.Id,
            UserId = s.UserId,
            AuthVersion = u.AuthVersion,
        };
}

internal sealed class ValidSession
{
    public Guid SessionId { get; init; }

    public Guid UserId { get; init; }

    public int AuthVersion { get; init; }
}
