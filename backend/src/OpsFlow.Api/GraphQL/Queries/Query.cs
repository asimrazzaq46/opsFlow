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

/// <summary>
/// Read-only GraphQL surface. Every resolver delegates to the SAME Application services as the REST endpoints, so tenant scoping
/// and role visibility are enforced once (TicketAccess.Visible / ActorAccess), not re-implemented per resolver. Results are flat,
/// fully authorized read models (no entity graph), so no nested resolver can reach unauthorized data.
/// </summary>
public sealed class Query
{
    [Serial]
    [Cost(10)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<PagedResult<TicketSummaryDto>> Tickets(
        [Service] TicketService svc,
        TicketFilterInput? filter,
        CancellationToken ct
    ) => svc.ListAsync(PageGuard.Check(filter ?? new TicketFilterInput()).ToFilter(), ct);

    [Serial]
    [Cost(5)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<TicketDetailDto> Ticket(
        [Service] TicketService svc,
        Guid id,
        CancellationToken ct
    ) => svc.GetAsync(id, ct);

    [Serial]
    [Cost(10)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<PagedResult<TicketActivityDto>> TicketActivity(
        [Service] TicketService svc,
        Guid ticketId,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20
    ) => svc.GetActivityAsync(ticketId, PageGuard.Page(page), PageGuard.Size(pageSize), ct);

    [Serial]
    [Cost(5)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<TicketSlaDto> TicketSla(
        [Service] TicketService svc,
        Guid ticketId,
        CancellationToken ct
    ) => svc.GetSlaAsync(ticketId, ct);

    [Serial]
    [Cost(10)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<PagedResult<CustomerDto>> Customers(
        [Service] CustomerService svc,
        string? search,
        bool? isActive,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20
    ) => svc.ListAsync(search, isActive, PageGuard.Page(page), PageGuard.Size(pageSize), ct);

    [Serial]
    [Cost(5)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<CustomerDto> Customer(
        [Service] CustomerService svc,
        Guid id,
        CancellationToken ct
    ) => svc.GetAsync(id, ct);

    [Serial]
    [Cost(10)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<PagedResult<MemberDto>> TeamMembers(
        [Service] MembershipService svc,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20
    ) => svc.ListAsync(PageGuard.Page(page), PageGuard.Size(pageSize), ct);

    [Serial]
    [Cost(30)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<DashboardDto> Dashboard(
        [Service] AnalyticsService svc,
        AnalyticsFilterInput? filter,
        CancellationToken ct
    ) => svc.GetDashboardAsync((filter ?? new AnalyticsFilterInput()).ToFilter(), ct);

    [Serial]
    [Cost(60)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public Task<ReportDto> Report(
        [Service] AnalyticsService svc,
        AnalyticsFilterInput? filter,
        CancellationToken ct
    ) => svc.GetReportAsync((filter ?? new AnalyticsFilterInput()).ToFilter(), ct);

    /// <summary>Kanban columns come from the real workflow statuses; each column is one bounded, filtered ticket page.</summary>
    [Serial]
    [Cost(40)] // UPDATED: har root field ka asli weight (analytics = kai aggregate queries)
    public async Task<IReadOnlyList<KanbanColumn>> KanbanBoard(
        [Service] TicketService svc,
        KanbanFilterInput? filter,
        CancellationToken ct,
        int perColumn = 20
    )
    {
        var take = PageGuard.Size(perColumn, 50);
        var f = filter ?? new KanbanFilterInput();
        var columns = new List<KanbanColumn>();
        foreach (
            var status in Enum.GetValues<TicketStatus>()
                .Where(s => s != TicketStatus.Closed || f.IncludeClosed)
        )
        {
            var page = await svc.ListAsync(
                new TicketListFilter(
                    [status],
                    f.Priority,
                    f.CategoryId,
                    f.AssignedMembershipId,
                    null,
                    null,
                    null,
                    f.Overdue == true ? TicketState.Overdue : null,
                    f.Search,
                    "updatedAt",
                    true,
                    Math.Max(f.Page ?? 1, 1),
                    take
                ),
                ct
            );
            columns.Add(new KanbanColumn(status, page.TotalCount, page.Items));
        }

        return columns;
    }
}
