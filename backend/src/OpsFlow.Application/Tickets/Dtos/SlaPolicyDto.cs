using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Outbox;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Application.Tickets;

public sealed record SlaPolicyDto(
    Guid Id,
    TicketPriority Priority,
    int ResponseMinutes,
    int ResolutionMinutes,
    bool IsActive
);
