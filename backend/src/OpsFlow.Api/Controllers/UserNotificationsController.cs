using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Common;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

/// <summary>The caller's own notifications across all their workspaces. Per-notification reads stay on the tenant route.</summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
[Tags("Notifications")]
public sealed class UserNotificationsController(NotificationService notifications) : ControllerBase
{
    [HttpGet(Name = "ListMyNotifications")]
    [EndpointSummary("List my notifications across workspaces")]
    [EndpointDescription(
        "Returns the caller's notification history from every workspace where their membership is active, newest first and paged. Each item names its workspace through tenantId."
    )]
    public async Task<ActionResult<PagedResult<NotificationDto>>> ListMyNotifications(
        CancellationToken ct,
        bool unreadOnly = false,
        int page = 1,
        int pageSize = 30
    )
    {
        return Ok(await notifications.ListAcrossWorkspacesAsync(unreadOnly, page, pageSize, ct));
    }

    [HttpGet("unread-count", Name = "GetMyUnreadNotificationCount")]
    [EndpointSummary("Get my unread notification count across workspaces")]
    [EndpointDescription("Returns how many unread notifications the caller has in all workspaces where their membership is active.")]
    public async Task<ActionResult<UnreadCountDto>> GetMyUnreadNotificationCount(CancellationToken ct)
    {
        return Ok(await notifications.UnreadCountAcrossWorkspacesAsync(ct));
    }

    [HttpPost("read-all", Name = "MarkAllMyNotificationsRead")]
    [EndpointSummary("Mark all my notifications as read across workspaces")]
    [EndpointDescription("Marks every unread notification of the caller as read in all workspaces where their membership is active.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllMyNotificationsRead(CancellationToken ct)
    {
        await notifications.MarkAllReadAcrossWorkspacesAsync(ct);
        return NoContent();
    }
}
