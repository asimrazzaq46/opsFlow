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

public sealed record TicketFilterInput(
    IReadOnlyList<TicketStatus>? Statuses = null,
    TicketPriority? Priority = null,
    Guid? CategoryId = null,
    Guid? AssignedMembershipId = null,
    Guid? CustomerId = null,
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null,
    TicketState? State = null,
    string? Search = null,
    string? SortBy = null,
    bool Descending = true,
    int Page = 1,
    int PageSize = 20
)
{
    public TicketListFilter ToFilter() =>
        new(
            Statuses,
            Priority,
            CategoryId,
            AssignedMembershipId,
            CustomerId,
            CreatedFrom,
            CreatedTo,
            State,
            Search,
            SortBy,
            Descending,
            Page,
            PageSize
        );
}
