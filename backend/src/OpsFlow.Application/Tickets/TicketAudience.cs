using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Who may receive private ticket data RIGHT NOW, computed from the database at publish time (never from a cached group):
/// active Owners/Managers, Agents that are assigned to or created the ticket, and the customer contact's linked user. Suspended,
/// deactivated, unassigned or re-roled people are therefore never targeted.
/// </summary>
public sealed class TicketAudience(IOpsFlowDbContext db)
{
    public async Task<List<Guid>> StaffAsync(Guid ticketId, Guid createdByUserId, CancellationToken ct)
    {
        var active = db.Memberships.AsNoTracking().Where(m => m.Status == MembershipStatus.Active && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active));
        var managers = active.Where(m => m.Role == TenantRole.TenantOwner || m.Role == TenantRole.Manager).Select(m => m.UserId);
        var agents = active.Where(m => m.Role == TenantRole.Agent
                && (m.UserId == createdByUserId || db.TicketAssignments.Any(a => a.TicketId == ticketId && a.MembershipId == m.Id && a.UnassignedAtUtc == null)))
            .Select(m => m.UserId);
        return await managers.Union(agents).ToListAsync(ct);
    }

    public async Task<Guid?> CustomerAsync(Guid? customerId, CancellationToken ct)
    {
        if (customerId is null)
        {
            return null;
        }

        return await (from c in db.Customers.AsNoTracking()
                      where c.Id == customerId && c.UserId != null
                      join m in db.Memberships on c.UserId equals m.UserId
                      where m.Role == TenantRole.Customer && m.Status == MembershipStatus.Active && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active)
                      select c.UserId).SingleOrDefaultAsync(ct);
    }
}
