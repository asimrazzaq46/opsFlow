using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>Workspace-specific employee details; empty values clear a field. Name and email belong to the user account.</summary>
/// <remarks>
/// CheckVersion + ExpectedUpdatedAtUtc (the profile version the editor saw, null when there was no profile) refuse a stale edit with
/// 409 MEMBER_CHANGED; without CheckVersion the last write wins.
/// </remarks>
public sealed record UpdateMemberProfileRequest(
    [StringLength(100)] string? JobTitle,
    [StringLength(100)] string? Department,
    [StringLength(40)] string? Phone,
    bool CheckVersion = false,
    DateTime? ExpectedUpdatedAtUtc = null
);

/// <summary>The complete set of skill ids the member should have; omitted skills are removed.</summary>
/// <remarks>ExpectedSkillIds, when given, is the set the editor saw; a different current set is refused with 409 MEMBER_CHANGED.</remarks>
public sealed record SetMemberSkillsRequest([Required, MaxLength(30)] Guid[]? SkillIds, [MaxLength(30)] Guid[]? ExpectedSkillIds = null);

public sealed record CreateSkillRequest([Required, StringLength(50, MinimumLength = 1)] string Name);

public sealed record UpdateSkillRequest([Required, StringLength(50, MinimumLength = 1)] string Name, bool IsActive);

/// <summary>Memberships that should gain or lose ONE skill; every other skill of those members is left alone.</summary>
/// <remarks>The two lists must not overlap. Members that already match are skipped; the whole request is refused if any member is not allowed.</remarks>
public sealed record UpdateSkillAssignmentsRequest([MaxLength(100)] Guid[]? AddMembershipIds = null, [MaxLength(100)] Guid[]? RemoveMembershipIds = null);
