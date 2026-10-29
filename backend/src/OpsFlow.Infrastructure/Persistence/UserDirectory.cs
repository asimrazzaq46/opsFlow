using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;

namespace OpsFlow.Infrastructure.Persistence;

internal sealed class UserDirectory(OpsFlowDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.Email ?? string.Empty, u.DisplayName, u.EmailConfirmed))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
}
