using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Notifications;
using OpsFlow.Domain.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Outbox;

/// <summary>
/// Re-checks, at DELIVERY time, that the recipient may still receive the notification (active user, active membership of this tenant and
/// still in the ticket's audience). A delayed event therefore never discloses ticket data to someone who lost access in the meantime.
/// Unassigned agents are the intended exception: they are told that they were removed.
/// </summary>
public sealed class NotificationEligibility(IOpsFlowDbContext db, TicketAudience audience)
{
    public async Task<bool> IsEligibleAsync(Notification n, CancellationToken ct)
    {
        var active = await db.Memberships.AsNoTracking().AnyAsync(m => m.UserId == n.UserId && m.Status == MembershipStatus.Active, ct)
            && await db.Users.AsNoTracking().AnyAsync(u => u.Id == n.UserId && u.Status == UserStatus.Active, ct);
        if (!active)
        {
            return false;
        }

        if (n.Type == NotificationType.TicketUnassigned)
        {
            return true;
        }

        var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == n.TicketId).Select(t => new { t.CreatedByUserId, t.CustomerId }).SingleOrDefaultAsync(ct);
        return ticket is not null
            && ((await audience.StaffAsync(n.TicketId, ticket.CreatedByUserId, ct)).Contains(n.UserId) || await audience.CustomerAsync(ticket.CustomerId, ct) == n.UserId);
    }
}

/// <summary>SignalR delivery through the existing <see cref="IRealtimePublisher"/>. Payloads are rebuilt from the database; ids in the payload let clients de-duplicate a re-delivery.</summary>
public sealed class RealtimeOutboxHandler(IOpsFlowDbContext db, NotificationEligibility eligibility, TicketAudience audience, TicketMessageMapper messages, TicketNoteMapper notes, IRealtimePublisher publisher, ITenantContext tenantContext)
    : IOutboxChannelHandler
{
    public OutboxChannels Channel => OutboxChannels.Realtime;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        try
        {
            switch (message.EventType)
            {
                case OutboxEvents.NotificationCreated:
                    await PublishNotificationAsync(Read<NotificationEvent>(message), ct);
                    break;
                case OutboxEvents.TicketMessageCreated:
                    await PublishMessageAsync(Read<TicketMessageEvent>(message), ct);
                    break;
                case OutboxEvents.TicketNoteCreated:
                    await PublishNoteAsync(Read<TicketNoteEvent>(message), ct);
                    break;
                default:
                    throw new OutboxPermanentException($"Unsupported realtime event type '{message.EventType}'.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new OutboxPermanentException("Invalid realtime event configuration.", ex); // transport failures are swallowed by the publisher itself
        }
    }

    private async Task PublishNotificationAsync(NotificationEvent ev, CancellationToken ct)
    {
        var n = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ev.NotificationId, ct);
        if (n is not null && await eligibility.IsEligibleAsync(n, ct))
        {
            await publisher.NotifyUserAsync(n.UserId.ToString(), new NotificationCreated(NotificationService.ToDto(n)), ct);
        }
    }

    private async Task PublishMessageAsync(TicketMessageEvent ev, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ev.TicketId, ct);
        var message = await db.TicketMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == ev.MessageId && m.TicketId == ev.TicketId, ct);
        if (ticket is null || message is null)
        {
            return;
        }

        // Recipients are recomputed from the database NOW; each audience gets its own view (customers never see staff identities).
        var tenantId = tenantContext.TenantId!.Value;
        var staff = await audience.StaffAsync(ticket.Id, ticket.CreatedByUserId, ct);
        await publisher.SendToUsersAsync(staff.ConvertAll(u => u.ToString()), new TicketMessageCreated(tenantId, ticket.Id, (await messages.MapAsync([message], false, ct))[0]), ct);
        if (await audience.CustomerAsync(ticket.CustomerId, ct) is { } customer)
        {
            await publisher.SendToUsersAsync([customer.ToString()], new TicketMessageCreated(tenantId, ticket.Id, (await messages.MapAsync([message], true, ct))[0]), ct);
        }
    }

    private async Task PublishNoteAsync(TicketNoteEvent ev, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ev.TicketId, ct);
        var note = await db.TicketNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == ev.NoteId && n.TicketId == ev.TicketId, ct);
        if (ticket is null || note is null)
        {
            return;
        }

        // Internal notes go to STAFF only; there is no customer delivery path at all.
        var staff = await audience.StaffAsync(ticket.Id, ticket.CreatedByUserId, ct);
        await publisher.NotifyUsersAsync(staff.ConvertAll(u => u.ToString()), new TicketNoteCreated(tenantContext.TenantId!.Value, ticket.Id, (await notes.MapAsync([note], ct))[0]), ct);
    }

    internal static T Read<T>(OutboxMessage message) =>
        JsonSerializer.Deserialize<T>(message.Payload) ?? throw new OutboxPermanentException($"Event {message.Id} has an unreadable payload.");
}

/// <summary>
/// Background e-mail of a notification. Carries only the ticket number/title (never message or note content). A failing SMTP server makes
/// this channel retry with backoff; it never touches the committed business data. SMTP has an unavoidable "sent but not acknowledged"
/// window, so a crash at that instant can send the e-mail twice.
/// </summary>
public sealed partial class EmailOutboxHandler(IOpsFlowDbContext db, NotificationEligibility eligibility, IEmailService email, IOptions<AppOptions> app, ILogger<EmailOutboxHandler> logger)
    : IOutboxChannelHandler
{
    public OutboxChannels Channel => OutboxChannels.Email;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        if (message.EventType != OutboxEvents.NotificationCreated)
        {
            throw new OutboxPermanentException($"Unsupported e-mail event type '{message.EventType}'.");
        }

        var n = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == RealtimeOutboxHandler.Read<NotificationEvent>(message).NotificationId, ct);
        if (n is null)
        {
            return;
        }

        var recipient = await db.Users.AsNoTracking().Where(u => u.Id == n.UserId && u.Status == UserStatus.Active && u.EmailVerifiedAtUtc != null).Select(u => u.Email).SingleOrDefaultAsync(ct);
        if (recipient is null || !await eligibility.IsEligibleAsync(n, ct))
        {
            LogSkipped(logger, message.Id);
            return;
        }

        var body = $"{n.Subject}{(n.Detail is null ? string.Empty : $" ({n.Detail})")}\n\nOpen OpsFlow to view it: {app.Value.BaseUrl}";
        await email.SendAsync(recipient, $"[OpsFlow] {Describe(n.Type)}: {n.Subject}", body, ct);
    }

    private static string Describe(NotificationType type) => type switch
    {
        NotificationType.TicketAssigned => "Ticket assigned to you",
        NotificationType.SlaWarning => "SLA deadline approaching",
        NotificationType.SlaBreached => "SLA breached",
        _ => type.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox event {EventId}: e-mail skipped (recipient no longer eligible)")]
    private static partial void LogSkipped(ILogger logger, Guid eventId);
}

/// <summary>Invalidation of tenant cache entries after a committed change; repeating it is harmless.</summary>
public sealed class CacheOutboxHandler(ITenantCache cache) : IOutboxChannelHandler
{
    public OutboxChannels Channel => OutboxChannels.Cache;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var ev = RealtimeOutboxHandler.Read<CacheInvalidatedEvent>(message);
        if (!await cache.InvalidateAsync(message.TenantId, ev.Resource, ct))
        {
            throw new InvalidOperationException("Cache backend unavailable; invalidation will be retried.");
        }
    }
}
