using OpsFlow.Domain.Tickets;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Tickets;

/// <summary>
/// Internal (staff-only) child task. <c>IsAssigneeEligible</c> is false when the responsible agent was deactivated, re-roled or removed
/// from the parent ticket: a manager must reassign the task. <c>CanEdit</c>/<c>CanChangeStatus</c> describe the caller's permissions.
/// </summary>
public sealed record TicketTaskDto(
    Guid Id,
    Guid TicketId,
    string Title,
    string? Description,
    TicketTaskStatus Status,
    int SortOrder,
    Guid AssigneeMembershipId,
    string AssigneeDisplayName,
    bool IsAssigneeEligible,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    int Version,
    bool CanEdit,
    bool CanChangeStatus
);

public sealed record TicketTaskSummaryDto(
    int TotalTasks,
    int TodoTasks,
    int InProgressTasks,
    int CompletedTasks
);

/// <summary><c>IsReadOnly</c> is true while the parent ticket is Resolved/Closed; <c>CanManage</c> = may assign and reorder.</summary>
public sealed record TicketTaskListDto(
    IReadOnlyList<TicketTaskDto> Tasks,
    TicketTaskSummaryDto Summary,
    bool IsReadOnly,
    bool CanCreate,
    bool CanManage
);

/// <summary>Kanban card indicator (internal staff only).</summary>
public sealed record TicketTaskProgressDto(Guid TicketId, int TotalTasks, int CompletedTasks);

public sealed record CreateTicketTaskCommand(
    string Title,
    string? Description,
    Guid? AssigneeMembershipId
);

public sealed record TaskOrderItem(Guid TaskId, int Version);

public static class TicketTaskChanges
{
    public const string Created = "TaskCreated";
    public const string Updated = "TaskUpdated";
    public const string StatusChanged = "TaskStatusChanged";
    public const string Assigned = "TaskAssigned";
    public const string Reordered = "TasksReordered";
}

/// <summary>Content-free realtime signal for internal staff; clients reload <c>GET .../tasks</c>.</summary>
[RealtimeEvent("ticket.task.changed")]
public sealed record TicketTaskChanged(
    Guid TenantId,
    Guid TicketId,
    Guid? TaskId,
    string Change,
    int? Version
);
