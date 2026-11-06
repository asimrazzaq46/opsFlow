using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Outbox;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Notifications;

/// <summary>
/// Persistent notifications. Writers call <see cref="StageAsync"/> BEFORE their SaveChanges: the notification rows AND their outbox events
/// commit atomically with the ticket change (the unique DedupKeys make retries harmless); the outbox dispatcher pushes/e-mails them after
/// the commit (StageAsync returns the number of recipients; 0 = nothing was staged). Readers only ever touch the caller's own rows.
/// </summary>
public sealed class NotificationService(
    IOpsFlowDbContext db,
    TicketAudience audience,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    OutboxWriter outbox,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider clock
)
{
    private const int MaxPageSize = 100;

    /// <summary>Adds notification rows for the ticket's current audience (minus the actor) plus any explicit users. Content is never copied.</summary>
    public async Task<int> StageAsync(
        Ticket ticket,
        NotificationType type,
        Guid actorUserId,
        string dedupKey,
        bool toStaff,
        bool toCustomer,
        IEnumerable<Guid>? alsoUsers = null,
        string? detail = null,
        CancellationToken ct = default
    )
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
            return 0; // no empty notification is created; the caller gets 0 (the SLA monitor logs a diagnostic)
        }

        var prefix =
            await db
                .Settings.AsNoTracking()
                .Select(s => s.TicketNumberPrefix)
                .SingleOrDefaultAsync(ct) ?? TenantSettings.DefaultTicketPrefix;
        var subject = $"{TenantSettings.FormatNumber(prefix, ticket.Number)} {ticket.Title}";
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var userId in recipients)
        {
            var notification = Notification.Create(
                ticket.TenantId,
                userId,
                type,
                ticket.Id,
                subject,
                detail,
                dedupKey,
                now
            );
            db.Notifications.Add(notification);
            // Live push/e-mail go through the outbox in this same commit, never a direct publish.
            var channels =
                OutboxChannels.Realtime
                | (
                    outboxOptions.Value.EmailNotificationTypes.Contains(type)
                        ? OutboxChannels.Email
                        : OutboxChannels.None
                );
            outbox.Stage(
                ticket.TenantId,
                OutboxEvents.NotificationCreated,
                ticket.Id,
                new NotificationEvent(notification.Id),
                $"notif:{userId}:{dedupKey}",
                channels
            );
        }

        return recipients.Count;
    }

    public async Task<PagedResult<NotificationDto>> ListAsync(
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = Mine().AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<NotificationDto>(rows.ConvertAll(ToDto), page, pageSize, total);
    }

    public async Task<UnreadCountDto> UnreadCountAsync(CancellationToken ct) =>
        new(await Mine().CountAsync(n => n.ReadAtUtc == null, ct));

    /// <returns>False when the notification does not exist for the caller (another user's is reported the same way).</returns>
    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await Mine()
            .Where(n => n.Id == id && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, (DateTime?)now), ct);
        return await Mine().AnyAsync(n => n.Id == id, ct);
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await Mine()
            .Where(n => n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, (DateTime?)now), ct);
    }

    /// <summary>Newest-first history of the caller across every workspace where their membership is currently active.</summary>
    public async Task<PagedResult<NotificationDto>> ListAcrossWorkspacesAsync(
        bool unreadOnly,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        (page, pageSize, var skip) = Paging.Normalize(page, pageSize, MaxPageSize);
        var query = MineAcrossWorkspaces().AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<NotificationDto>(rows.ConvertAll(ToDto), page, pageSize, total);
    }

    public async Task<UnreadCountDto> UnreadCountAcrossWorkspacesAsync(CancellationToken ct) =>
        new(await MineAcrossWorkspaces().CountAsync(n => n.ReadAtUtc == null, ct));

    public async Task MarkAllReadAcrossWorkspacesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await MineAcrossWorkspaces()
            .Where(n => n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, (DateTime?)now), ct);
    }

    // Recipient ownership plus a CURRENTLY active membership in the notification's own tenant: a removed or suspended member sees nothing
    // of that workspace. The tenant query filter is lifted on purpose, so both conditions are explicit.
    private IQueryable<Notification> MineAcrossWorkspaces()
    {
        var userId = currentUser.UserId;
        return db
            .Notifications.IgnoreQueryFilters()
            .Where(n =>
                n.UserId == userId
                && db.Memberships.IgnoreQueryFilters()
                    .Any(m => m.UserId == userId && m.TenantId == n.TenantId && m.Status == MembershipStatus.Active)
            );
    }

    // Tenant filter + current user: a notification of another user (or tenant) is simply "not found".
    private IQueryable<Notification> Mine()
    {
        var userId = currentUser.UserId;
        return db.Notifications.Where(n =>
            n.UserId == userId && n.TenantId == tenantContext.TenantId
        );
    }

    public static NotificationDto ToDto(Notification n) =>
        new(
            n.Id,
            n.TenantId,
            n.Type,
            n.TicketId,
            n.Subject,
            n.Detail,
            n.CreatedAtUtc,
            n.ReadAtUtc is not null
        );
}
