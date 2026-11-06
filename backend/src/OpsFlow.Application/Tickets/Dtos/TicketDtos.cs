using OpsFlow.Domain.Tickets;
using OpsFlow.Realtime;

namespace OpsFlow.Application.Tickets;

public static class TicketChanges
{
    public const string Created = "TicketCreated";
    public const string Updated = "TicketUpdated";
    public const string StatusChanged = "TicketStatusChanged";
    public const string Reopened = "TicketReopened";
    public const string Assignment = "TicketAssignmentChanged";
}

/// <summary>Content-free realtime signal for the staff of a workspace: something about this ticket changed, reload the list or board.</summary>
[RealtimeEvent("ticket.changed")]
public sealed record TicketChanged(Guid TenantId, Guid TicketId, string Change);

/// <summary>Content-free signal to the people who may list this ticket's attachments; clients reload <c>GET .../attachments</c>.</summary>
[RealtimeEvent("ticket.attachment.added")]
public sealed record TicketAttachmentAdded(Guid TenantId, Guid TicketId);

/// <summary>Sent only to the affected portal user: whether new tickets may be opened in this workspace changed. Clients re-read the authoritative state.</summary>
[RealtimeEvent("customer.eligibility.changed")]
public sealed record CustomerEligibilityChanged(Guid TenantId, bool CanCreateTickets);

/// <summary>Whether the caller may open a new ticket now. <c>Reason</c> is NotLinked or Inactive for a Customer who may not.</summary>
public sealed record TicketCreationEligibilityDto(bool CanCreate, string? Reason);

public sealed record TicketSummaryDto(
    Guid Id,
    long Number,
    string DisplayNumber,
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    Guid CategoryId,
    string CategoryName,
    Guid? CustomerId,
    string? CustomerName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ResolvedAtUtc,
    int Version,
    int? AssigneeCount,
    int? EligibleAssigneeCount,
    DateTime? ResolutionDueAtUtc,
    bool? IsOverdue,
    // Whether the caller holds an open assignment (the condition the status and reopen commands check for Agents). Null for Customers.
    bool? AssignedToMe,
    // Current assignees of list pages (one batched query per page); null for Customers and for single-ticket reads, which carry them separately.
    IReadOnlyList<AssigneeDto>? Assignees = null,
    // Unread public chat messages of list pages for the caller (one grouped query per page); null for single-ticket reads.
    int? UnreadMessageCount = null
);

/// <summary><c>IsAgentActive</c> is true only while the assignee is still ELIGIBLE (Agent role, active membership, active user).</summary>
public sealed record AssigneeDto(
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    DateTime AssignedAtUtc,
    bool IsAgentActive
);

public sealed record AssignmentHistoryDto(
    Guid Id,
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    DateTime AssignedAtUtc,
    Guid AssignedByMembershipId,
    DateTime? UnassignedAtUtc,
    Guid? UnassignedByMembershipId,
    bool IsActive
);

public sealed record SlaCycleDto(
    Guid Id,
    int CycleNumber,
    SlaCycleReason Reason,
    DateTime StartedAtUtc,
    int ResponseMinutes,
    int ResolutionMinutes,
    DateTime? ResponseDueAtUtc,
    DateTime ResolutionDueAtUtc,
    DateTime? FirstResponseAtUtc,
    DateTime? ResolvedAtUtc,
    SlaState ResponseState,
    SlaState ResolutionState,
    double? RemainingResolutionMinutes
);

/// <summary>Staff-only SLA view: the current (active, else latest) cycle plus the complete cycle history.</summary>
public sealed record TicketSlaDto(SlaCycleDto? Current, IReadOnlyList<SlaCycleDto> Cycles);

/// <summary>Assignees, SLA and the creating staff member are staff-only: for Customers they are null.</summary>
public sealed record TicketDetailDto(
    TicketSummaryDto Summary,
    string Description,
    Guid? CreatedByUserId,
    IReadOnlyList<AssigneeDto>? Assignees,
    SlaCycleDto? CurrentSla
);

public sealed record TicketActivityDto(
    Guid Id,
    int Sequence,
    TicketActivityType Type,
    DateTime OccurredAtUtc,
    Guid? ActorUserId,
    string? OldValue,
    string? NewValue,
    // Who acted: the persisted actor's display name (a Customer only ever gets their own name, never a staff identity). Null for
    // automation and for staff actions seen by a Customer.
    string? ActorDisplayName = null,
    // For assignment entries: the agent who was added or removed.
    string? RelatedDisplayName = null
);

public sealed record CreateTicketCommand(
    string Title,
    string Description,
    TicketPriority? Priority,
    Guid CategoryId,
    Guid? CustomerId,
    IReadOnlyList<Guid>? AssigneeMembershipIds
);

public sealed record UpdateTicketCommand(
    string? Title,
    string? Description,
    TicketPriority? Priority,
    Guid? CategoryId,
    int? Version
);

public sealed record TicketListFilter(
    IReadOnlyList<TicketStatus>? Statuses,
    TicketPriority? Priority,
    Guid? CategoryId,
    Guid? AssignedMembershipId,
    Guid? CustomerId,
    DateTime? CreatedFrom,
    DateTime? CreatedTo,
    TicketState? State,
    string? Search,
    string? SortBy,
    bool Descending,
    int Page,
    int PageSize,
    bool AssignedToMe = false
);
