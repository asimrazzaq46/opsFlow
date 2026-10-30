using System.ComponentModel.DataAnnotations;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Contracts;

public sealed record CreateSlaPolicyRequest(
    [EnumDataType(typeof(TicketPriority))] TicketPriority Priority,
    [Range(1, SlaPolicy.MaxMinutes)] int ResponseMinutes,
    [Range(1, SlaPolicy.MaxMinutes)] int ResolutionMinutes
);
