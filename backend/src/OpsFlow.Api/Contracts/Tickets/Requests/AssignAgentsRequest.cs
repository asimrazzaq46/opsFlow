using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

public sealed record AssignAgentsRequest(
    [Required, MinLength(1), MaxLength(20)] List<Guid> MembershipIds,
    int? Version
);
