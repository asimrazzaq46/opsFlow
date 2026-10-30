using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

// Request contracts: validation attributes sit on the record parameters (MVC binds records through the constructor and rejects property-targeted attributes).
public sealed record CustomerRequest(
    [Required, StringLength(150, MinimumLength = 2)] string Name,
    [Required, EmailAddress, StringLength(256)] string Email,
    [StringLength(50)] string? Phone,
    Guid? LinkedUserId);

public sealed record CategoryRequest([Required, StringLength(100, MinimumLength = 2)] string Name);

public sealed record CreateSlaPolicyRequest(
    [EnumDataType(typeof(TicketPriority))] TicketPriority Priority,
    [Range(1, SlaPolicy.MaxMinutes)] int ResponseMinutes,
    [Range(1, SlaPolicy.MaxMinutes)] int ResolutionMinutes);

public sealed record UpdateSlaPolicyRequest(
    [Range(1, SlaPolicy.MaxMinutes)] int ResponseMinutes,
    [Range(1, SlaPolicy.MaxMinutes)] int ResolutionMinutes);

public sealed record CreateTicketRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Title,
    [Required, StringLength(4000, MinimumLength = 1)] string Description,
    [EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    [Required] Guid CategoryId,
    Guid? CustomerId,
    [MaxLength(20)] List<Guid>? AssigneeMembershipIds);

/// <summary>Only these fields can be edited; ownership, requester, number and status are never client-writable. <c>Version</c> (optional) guards against lost updates.</summary>
public sealed record UpdateTicketRequest(
    [StringLength(200, MinimumLength = 3)] string? Title,
    [StringLength(4000, MinimumLength = 1)] string? Description,
    [EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    Guid? CategoryId,
    int? Version);

public sealed record TransitionRequest([EnumDataType(typeof(TicketStatus))] TicketStatus Status, int? Version);

public sealed record ReopenRequest(int? Version);

public sealed record AssignAgentsRequest([Required, MinLength(1), MaxLength(20)] List<Guid> MembershipIds, int? Version);

public sealed record UnassignAgentRequest(int? Version);

/// <summary>Query-string filters for the ticket list; every filter only narrows what the caller is already authorized to see.</summary>
public sealed record TicketListQuery(
    TicketStatus[]? Status,
    TicketPriority? Priority,
    Guid? CategoryId,
    Guid? AssignedMembershipId,
    Guid? CustomerId,
    DateTime? CreatedFrom,
    DateTime? CreatedTo,
    TicketState? State,
    string? Search,
    string? SortBy,
    bool Descending = false,
    int Page = 1,
    int PageSize = 25);
