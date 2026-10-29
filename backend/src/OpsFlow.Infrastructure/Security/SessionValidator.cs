using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Auth;
using OpsFlow.Domain.Users;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Security;

/// <summary>One indexed query per request: the JWT is only a pointer, the database decides whether it is still honoured.</summary>
internal sealed class SessionValidator(OpsFlowDbContext db, TimeProvider clock) : ISessionValidator
{
    public Task<bool> IsValidAsync(Guid userId, Guid sessionId, int authVersion, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.UserSessions.AsNoTracking().AnyAsync(
            s => s.Id == sessionId && s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now
                && db.Users.Any(u => u.Id == userId && u.Status == UserStatus.Active && u.AuthVersion == authVersion),
            cancellationToken);
    }
}
