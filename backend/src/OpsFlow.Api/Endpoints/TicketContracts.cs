using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Endpoints;

// Public, top-level request contracts (the validation source generator only discovers accessible types).
public sealed record CustomerRequest(
    [property: Required, StringLength(150, MinimumLength = 2)] string Name,
    [property: Required, EmailAddress, StringLength(256)] string Email,
    [property: StringLength(50)] string? Phone,
    Guid? LinkedUserId);

public sealed record CategoryRequest([property: Required, StringLength(100, MinimumLength = 2)] string Name);

public sealed record CreateSlaPolicyRequest(
    [property: EnumDataType(typeof(TicketPriority))] TicketPriority Priority,
    [property: Range(1, SlaPolicy.MaxMinutes)] int ResponseMinutes,
    [property: Range(1, SlaPolicy.MaxMinutes)] int ResolutionMinutes);

public sealed record UpdateSlaPolicyRequest(
    [property: Range(1, SlaPolicy.MaxMinutes)] int ResponseMinutes,
    [property: Range(1, SlaPolicy.MaxMinutes)] int ResolutionMinutes);

public sealed record CreateTicketRequest(
    [property: Required, StringLength(200, MinimumLength = 3)] string Title,
    [property: Required, StringLength(4000, MinimumLength = 1)] string Description,
    [property: EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    [property: Required] Guid CategoryId,
    Guid? CustomerId,
    [property: MaxLength(20)] List<Guid>? AssigneeMembershipIds);

/// <summary>Only these fields can be edited; ownership, requester, number and status are never client-writable. <c>Version</c> (optional) guards against lost updates.</summary>
public sealed record UpdateTicketRequest(
    [property: StringLength(200, MinimumLength = 3)] string? Title,
    [property: StringLength(4000, MinimumLength = 1)] string? Description,
    [property: EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    Guid? CategoryId,
    int? Version);

public sealed record TransitionRequest([property: EnumDataType(typeof(TicketStatus))] TicketStatus Status, int? Version);

public sealed record ReopenRequest(int? Version);

public sealed record AssignAgentsRequest([property: Required, MinLength(1), MaxLength(20)] List<Guid> MembershipIds, int? Version);

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
