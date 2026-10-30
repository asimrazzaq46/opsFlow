using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/notifications")]
[Authorize(Policy = AuthPolicies.TenantMember)] // every operation only ever touches the caller's own notifications in this tenant
[Tags("Notifications")]
public sealed class NotificationsController(NotificationService notifications) : ControllerBase
{
    [HttpGet(Name = "ListNotifications")]
    [EndpointSummary("List notifications")]
    [EndpointDescription("Returns the caller's notification history in this tenant, newest first and paged; unreadOnly narrows it. Use it to catch up after a reconnect. Requires an active membership in the tenant.")]
    public async Task<ActionResult<PagedResult<NotificationDto>>> ListNotifications(CancellationToken ct, bool unreadOnly = false, int page = 1, int pageSize = 30)
    {
        return Ok(await notifications.ListAsync(unreadOnly, page, pageSize, ct));
    }

    [HttpGet("unread-count", Name = "GetUnreadNotificationCount")]
    [EndpointSummary("Get unread notification count")]
    [EndpointDescription("Returns how many unread notifications the caller has in this tenant. Requires an active membership in the tenant.")]
    public async Task<ActionResult<UnreadCountDto>> GetUnreadNotificationCount(CancellationToken ct)
    {
        return Ok(await notifications.UnreadCountAsync(ct));
    }

    [HttpPost("{notificationId:guid}/read", Name = "MarkNotificationRead")]
    [EndpointSummary("Mark notification as read")]
    [EndpointDescription("Marks one of the caller's own notifications as read (idempotent). Another user's notification is reported as not found. Requires an active membership in the tenant.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkNotificationRead(Guid notificationId, CancellationToken ct)
    {
        await notifications.MarkReadAsync(notificationId, ct);
        return NoContent();
    }

    [HttpPost("read-all", Name = "MarkAllNotificationsRead")]
    [EndpointSummary("Mark all notifications as read")]
    [EndpointDescription("Marks every unread notification of the caller in this tenant as read. Requires an active membership in the tenant.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllNotificationsRead(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(ct);
        return NoContent();
    }
}
