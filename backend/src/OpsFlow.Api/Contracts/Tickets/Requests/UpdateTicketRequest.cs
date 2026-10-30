using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

/// <summary>Only these fields can be edited; ownership, requester, number and status are never client-writable. <c>Version</c> (optional) guards against lost updates.</summary>
public sealed record UpdateTicketRequest(
    [StringLength(200, MinimumLength = 3)] string? Title,
    [StringLength(4000, MinimumLength = 1)] string? Description,
    [EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    Guid? CategoryId,
    int? Version
);
