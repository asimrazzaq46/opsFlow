using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Tickets;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Notifications;

/// <summary>
/// Persistent notifications. Writers call <see cref="StageAsync"/> BEFORE their SaveChanges (so the rows commit atomically with the
/// ticket change and the unique DedupKey makes retries harmless) and <see cref="FlushAsync"/> AFTER the commit to push them live.
/// Readers only ever touch the caller's own rows.
/// </summary>
public sealed class NotificationService(IOpsFlowDbContext db, TicketAudience audience, ICurrentUser currentUser, ITenantContext tenantContext, IRealtimePublisher publisher, TimeProvider clock)
{
    private const int MaxPageSize = 100;

    private readonly List<Notification> _pending = [];

    /// <summary>Adds notification rows for the ticket's current audience (minus the actor) plus any explicit users. Content is never copied.</summary>
    public async Task StageAsync(Ticket ticket, NotificationType type, Guid actorUserId, string dedupKey, bool toStaff, bool toCustomer, IEnumerable<Guid>? alsoUsers = null, string? detail = null, CancellationToken ct = default)
    {
        HashSet<Guid> recipients = [.. alsoUsers ?? []];
        if (toStaff)
        {
            recipients.UnionWith(await audience.StaffAsync(ticket.Id, ticket.CreatedByUserId, ct));
        }

        if (toCustomer && await audience.CustomerAsync(ticket.CustomerId, ct) is { } customer)
        {
            recipients.Add(customer);
        }

        recipients.Remove(actorUserId);
        if (recipients.Count == 0)
        {
            return;
        }

        var prefix = await db.Settings.AsNoTracking().Select(s => s.TicketNumberPrefix).SingleOrDefaultAsync(ct) ?? TenantSettings.DefaultTicketPrefix;
        var subject = $"{TenantSettings.FormatNumber(prefix, ticket.Number)} {ticket.Title}";
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var userId in recipients)
        {
            var notification = Notification.Create(ticket.TenantId, userId, type, ticket.Id, subject, detail, dedupKey, now);
            db.Notifications.Add(notification);
            _pending.Add(notification);
        }
    }

    /// <summary>Pushes the committed notifications to their owners. Best effort: clients re-sync from the history endpoint after a reconnect.</summary>
    public async Task FlushAsync()
    {
        // sirf wo rows jo wakai commit hui (Unchanged); retry mein discard hui rows kabhi publish nahi hoti
        var committed = db.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Unchanged).Select(e => e.Entity).ToHashSet();
        var batch = _pending.Where(committed.Contains).ToList();
        _pending.Clear();
        foreach (var n in batch)
        {
            await publisher.NotifyUserAsync(n.UserId.ToString(), new NotificationCreated(ToDto(n)));
        }
    }

    public async Task<PagedResult<NotificationDto>> ListAsync(bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = Mine().AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Skip(skip).Take(pageSize).ToListAsync(ct);
        return new PagedResult<NotificationDto>(rows.ConvertAll(ToDto), page, pageSize, total);
    }

    public async Task<UnreadCountDto> UnreadCountAsync(CancellationToken ct) => new(await Mine().CountAsync(n => n.ReadAtUtc == null, ct));

    public async Task MarkReadAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await Mine().Where(n => n.Id == id && n.ReadAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, (DateTime?)now), ct);
        if (!await Mine().AnyAsync(n => n.Id == id, ct))
        {
            throw AppException.NotFound("Notification not found.");
        }
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await Mine().Where(n => n.ReadAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, (DateTime?)now), ct);
    }

    // Tenant filter + current user: a notification of another user (or tenant) is simply "not found".
    private IQueryable<Notification> Mine()
    {
        var userId = currentUser.UserId;
        return db.Notifications.Where(n => n.UserId == userId && n.TenantId == tenantContext.TenantId);
    }

    private static NotificationDto ToDto(Notification n) => new(n.Id, n.TenantId, n.Type, n.TicketId, n.Subject, n.Detail, n.CreatedAtUtc, n.ReadAtUtc is not null);
}
