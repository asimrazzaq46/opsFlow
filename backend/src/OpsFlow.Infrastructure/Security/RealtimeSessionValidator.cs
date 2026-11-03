using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Auth;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Realtime;

namespace OpsFlow.Infrastructure.Security;

/// <summary>
/// Applies the per-request session rule (<see cref="SessionValidator.ValidSessions"/>) to live SignalR connections: one query per batch,
/// never one per connection. Identity comes only from the principal the connection authenticated with, and a connection is valid only when its
/// exact (user, session, auth version) triple matches, so revoking one device never closes another device's connection.
/// </summary>
internal sealed class RealtimeSessionValidator(OpsFlowDbContext db, TimeProvider clock)
    : IRealtimeConnectionValidator
{
    public async Task<IReadOnlySet<string>> FindValidAsync(
        IReadOnlyList<RealtimeConnectionInfo> connections,
        CancellationToken cancellationToken
    )
    {
        // A principal without session claims can never be valid: it is not asked about and therefore reported as invalid.
        List<(string ConnectionId, Guid UserId, Guid SessionId, int AuthVersion)> claimed = [];
        foreach (var connection in connections)
        {
            if (
                AuthClaims.TryReadSession(
                    connection.User,
                    out var userId,
                    out var sessionId,
                    out var authVersion
                )
            )
            {
                claimed.Add((connection.ConnectionId, userId, sessionId, authVersion));
            }
        }

        if (claimed.Count == 0)
        {
            return new HashSet<string>();
        }

        var sessionIds = claimed.Select(c => c.SessionId).Distinct().ToList();
        var valid = (
            await SessionValidator
                .ValidSessions(db, clock.GetUtcNow().UtcDateTime)
                .Where(v => sessionIds.Contains(v.SessionId))
                .ToListAsync(cancellationToken)
        )
            .Select(v => (v.SessionId, v.UserId, v.AuthVersion))
            .ToHashSet();

        return claimed
            .Where(c => valid.Contains((c.SessionId, c.UserId, c.AuthVersion)))
            .Select(c => c.ConnectionId)
            .ToHashSet();
    }
}
