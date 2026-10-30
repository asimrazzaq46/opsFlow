using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

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
    int PageSize = 25
);
