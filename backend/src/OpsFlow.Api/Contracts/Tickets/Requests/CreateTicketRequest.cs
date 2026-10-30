using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

public sealed record CreateTicketRequest(
    [Required, StringLength(200, MinimumLength = 3)] string Title,
    [Required, StringLength(4000, MinimumLength = 1)] string Description,
    [EnumDataType(typeof(TicketPriority))] TicketPriority? Priority,
    [Required] Guid CategoryId,
    Guid? CustomerId,
    [MaxLength(20)] List<Guid>? AssigneeMembershipIds
);
