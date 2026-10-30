using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

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
    bool? IsOverdue
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
    string? NewValue
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
    int PageSize
);
