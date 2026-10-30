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

public sealed record AnalyticsFilterInput(
    DateOnly? From = null,
    DateOnly? To = null,
    TicketPriority? Priority = null,
    Guid? CategoryId = null,
    TicketStatus? Status = null,
    Guid? AgentMembershipId = null,
    Guid? CustomerId = null
)
{
    public AnalyticsFilter ToFilter() =>
        new(From, To, Priority, CategoryId, Status, AgentMembershipId, CustomerId);
}
