using OpsFlow.Domain.Team;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Memberships;

public sealed record SkillDto(Guid Id, string Name, bool IsActive);

/// <summary>One staff member in the workspace directory. Email is null when the caller may not see it (Agents see only their own).</summary>
public sealed record TeamMemberRowDto(
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    string? Email,
    TenantRole Role,
    bool IsActive,
    string? JobTitle,
    string? Department,
    IReadOnlyList<SkillDto> Skills,
    int OpenAssignmentCount,
    DateTime JoinedAtUtc
);

/// <summary>
/// Counts from persisted data only. ResolveActions counts every Resolved action the member performed (a reopened and resolved-again
/// ticket counts again); ResolvedTicketsDistinct counts each ticket once. Being assigned is not counted as resolving.
/// </summary>
public sealed record TeamMemberStatsDto(
    int OpenAssignmentCount,
    int OpenTaskCount,
    int ResolveActions,
    int ResolvedTicketsDistinct
);

public sealed record AssignedTicketDto(
    Guid TicketId,
    string DisplayNumber,
    string Title,
    TicketStatus Status,
    TicketPriority Priority
);

public sealed record MemberActivityDto(
    Guid TicketId,
    string DisplayNumber,
    TicketActivityType Type,
    DateTime OccurredAtUtc
);

public sealed record AuditEntryDto(AuditAction Action, string? ActorDisplayName, string? Detail, DateTime OccurredAtUtc);

/// <summary>
/// Permissions are the server's decision for THIS caller; the client only uses them to shape the screen. ProfileUpdatedAtUtc is sent back
/// on a profile edit so a concurrent change by another administrator is not overwritten.
/// </summary>
public sealed record TeamMemberDetailDto(
    TeamMemberRowDto Member,
    string? Phone,
    DateTime? ProfileUpdatedAtUtc,
    bool CanEditProfileAndSkills,
    bool CanChangeRoleAndStatus,
    TeamMemberStatsDto Stats,
    IReadOnlyList<AssignedTicketDto> AssignedTickets,
    IReadOnlyList<MemberActivityDto> RecentActivity,
    IReadOnlyList<AuditEntryDto>? AdminHistory
);

/// <summary>ExpectedUpdatedAtUtc: the profile version the editor saw (null = no profile yet); omitted by callers that accept last-write-wins.</summary>
public sealed record UpdateMemberProfileCommand(
    string? JobTitle,
    string? Department,
    string? Phone,
    bool CheckVersion = false,
    DateTime? ExpectedUpdatedAtUtc = null
);
