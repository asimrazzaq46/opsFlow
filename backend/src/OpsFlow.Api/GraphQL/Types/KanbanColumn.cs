using HotChocolate;
using HotChocolate.CostAnalysis.Types;
using HotChocolate.Types;
using OpsFlow.Application.Analytics;
using OpsFlow.Application.Common;
using OpsFlow.Application.Customers;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Tickets;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.GraphQL;

public sealed record KanbanColumn(
    TicketStatus Status,
    int Total,
    IReadOnlyList<TicketSummaryDto> Tickets
);
