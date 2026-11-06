using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>Workspace-specific employee details; empty values clear a field. Name and email belong to the user account.</summary>
public sealed record UpdateMemberProfileRequest(
    [StringLength(100)] string? JobTitle,
    [StringLength(100)] string? Department,
    [StringLength(40)] string? Phone
);

/// <summary>The complete set of skill ids the member should have; omitted skills are removed.</summary>
public sealed record SetMemberSkillsRequest([Required, MaxLength(30)] Guid[]? SkillIds);

public sealed record CreateSkillRequest([Required, StringLength(50, MinimumLength = 1)] string Name);

public sealed record UpdateSkillRequest([Required, StringLength(50, MinimumLength = 1)] string Name, bool IsActive);
