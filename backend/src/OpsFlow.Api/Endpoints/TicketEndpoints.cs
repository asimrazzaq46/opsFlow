using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Endpoints;

internal static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        // Authorization (who sees/changes what) is enforced inside the services from the CURRENT database state, per role.
        var g = app.MapGroup("/api/v1/tenants/{tenantId:guid}/tickets").WithTags("Tickets").RequireAuthorization(AuthPolicies.TenantMember);

        g.MapPost("/", Create).WithName("CreateTicket")
            .WithSummary("Create service ticket")
            .WithDescription("Creates a tenant-scoped service ticket, initializes its SLA cycle and records activity. Requires an active membership in the tenant; Customers only see their own tickets.")
            .ProducesConflict();
        g.MapGet("/", List).WithName("ListTickets")
            .WithSummary("List tickets")
            .WithDescription("Returns the tickets visible to the caller, with filters, sorting and capped paging; staff can filter state=Unstaffed. Requires an active membership in the tenant; Customers only see their own tickets.");
        g.MapGet("/{ticketId:guid}", Get).WithName("GetTicket")
            .WithSummary("Get ticket")
            .WithDescription("Returns one visible ticket. Customers never receive assignees, SLA or staff identities. Requires an active membership in the tenant; Customers only see their own tickets.");
        g.MapPut("/{ticketId:guid}", Update).WithName("UpdateTicket")
            .WithSummary("Update ticket")
            .WithDescription("Edits title, description, priority or category, guarded by the optimistic-concurrency version. Requires an active membership in the tenant; Customers only see their own tickets.")
            .ProducesConflict();
        g.MapPost("/{ticketId:guid}/status", Transition).WithName("ChangeTicketStatus")
            .WithSummary("Change ticket status")
            .WithDescription("Moves the ticket along the allowed workflow transitions. Requires an active membership in the tenant; Customers only see their own tickets.")
            .ProducesConflict();
        g.MapPost("/{ticketId:guid}/reopen", Reopen).WithName("ReopenTicket")
            .WithSummary("Reopen ticket")
            .WithDescription("Reopens a resolved or closed ticket and starts a new SLA cycle. Requires an active membership in the tenant; Customers only see their own tickets.")
            .ProducesConflict();
        g.MapGet("/{ticketId:guid}/activity", Activity).WithName("ListTicketActivity")
            .WithSummary("List ticket activity")
            .WithDescription("Returns the append-only ticket timeline, paged. Requires an Owner, Manager or Agent role in the tenant.");
        g.MapGet("/{ticketId:guid}/sla", Sla).WithName("GetTicketSla")
            .WithSummary("Get ticket SLA")
            .WithDescription("Returns the current SLA cycle and the full cycle history. Requires an Owner, Manager or Agent role in the tenant.");
        g.MapGet("/{ticketId:guid}/assignments", Assignments).WithName("ListTicketAssignments")
            .WithSummary("List ticket assignments")
            .WithDescription("Returns current assignees, or the complete history when includeHistory=True. Requires an Owner, Manager or Agent role in the tenant.");
        g.MapPost("/{ticketId:guid}/assignments", Assign).WithName("AssignTicketAgents")
            .WithSummary("Assign agents")
            .WithDescription("Assigns one or more eligible agents to the ticket. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict();
        g.MapDelete("/{ticketId:guid}/assignments/{membershipId:guid}", Unassign).WithName("UnassignTicketAgent")
            .WithSummary("Unassign agent")
            .WithDescription("Closes an agent's assignment (history is kept). The last eligible assignee cannot be removed while the status needs one. Requires the Owner or Manager role in the tenant.")
            .ProducesConflict();
        return app;
    }

    private static async Task<Created<TicketDetailDto>> Create(Guid tenantId, CreateTicketRequest request, TicketService tickets, CancellationToken ct)
    {
        var ticket = await tickets.CreateAsync(new CreateTicketCommand(request.Title, request.Description, request.Priority, request.CategoryId, request.CustomerId, request.AssigneeMembershipIds), ct);
        return TypedResults.Created($"/api/v1/tenants/{tenantId}/tickets/{ticket.Summary.Id}", ticket);
    }

    private static async Task<Ok<PagedResult<TicketSummaryDto>>> List(Guid tenantId, [AsParameters] TicketListQuery q, TicketService tickets, CancellationToken ct) =>
        TypedResults.Ok(await tickets.ListAsync(
            new TicketListFilter(q.Status, q.Priority, q.CategoryId, q.AssignedMembershipId, q.CustomerId, q.CreatedFrom, q.CreatedTo, q.State, q.Search, q.SortBy, q.Descending, q.Page, q.PageSize), ct));

    private static async Task<Ok<TicketDetailDto>> Get(Guid tenantId, Guid ticketId, TicketService tickets, CancellationToken ct) =>
        TypedResults.Ok(await tickets.GetAsync(ticketId, ct));

    private static async Task<Ok<TicketDetailDto>> Update(Guid tenantId, Guid ticketId, UpdateTicketRequest request, TicketService tickets, CancellationToken ct) =>
        TypedResults.Ok(await tickets.UpdateAsync(ticketId, new UpdateTicketCommand(request.Title, request.Description, request.Priority, request.CategoryId, request.Version), ct));

    private static async Task<Ok<TicketDetailDto>> Transition(Guid tenantId, Guid ticketId, TransitionRequest request, TicketWorkflowService workflow, CancellationToken ct) =>
        TypedResults.Ok(await workflow.TransitionAsync(ticketId, request.Status, request.Version, ct));

    private static async Task<Ok<TicketDetailDto>> Reopen(Guid tenantId, Guid ticketId, ReopenRequest request, TicketWorkflowService workflow, CancellationToken ct) =>
        TypedResults.Ok(await workflow.ReopenAsync(ticketId, request.Version, ct));

    private static async Task<Ok<PagedResult<TicketActivityDto>>> Activity(Guid tenantId, Guid ticketId, TicketService tickets, CancellationToken ct, int page = 1, int pageSize = 50) =>
        TypedResults.Ok(await tickets.GetActivityAsync(ticketId, page, pageSize, ct));

    private static async Task<Ok<TicketSlaDto>> Sla(Guid tenantId, Guid ticketId, TicketService tickets, CancellationToken ct) =>
        TypedResults.Ok(await tickets.GetSlaAsync(ticketId, ct));

    private static async Task<Ok<IReadOnlyList<AssignmentHistoryDto>>> Assignments(Guid tenantId, Guid ticketId, TicketAssignmentService assignments, CancellationToken ct, bool includeHistory = false) =>
        TypedResults.Ok(await assignments.ListAsync(ticketId, includeHistory, ct));

    private static async Task<Ok<TicketDetailDto>> Assign(Guid tenantId, Guid ticketId, AssignAgentsRequest request, TicketAssignmentService assignments, CancellationToken ct) =>
        TypedResults.Ok(await assignments.AssignAsync(ticketId, request.MembershipIds, request.Version, ct));

    private static async Task<Ok<TicketDetailDto>> Unassign(Guid tenantId, Guid ticketId, Guid membershipId, TicketAssignmentService assignments, CancellationToken ct, int? version = null) =>
        TypedResults.Ok(await assignments.UnassignAsync(ticketId, membershipId, version, ct));
}
