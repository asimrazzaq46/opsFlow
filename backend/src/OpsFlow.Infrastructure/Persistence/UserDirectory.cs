using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;

namespace OpsFlow.Infrastructure.Persistence;

internal sealed class UserDirectory(OpsFlowDbContext db) : IUserDirectory
{
    // UPDATED: ab domain User table se (Identity users hata diye)
    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken
    ) =>
        await db
            .Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserSummary(
                u.Id,
                u.Email,
                u.DisplayName,
                u.EmailVerifiedAtUtc != null
            ))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
}
