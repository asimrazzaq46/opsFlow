using Microsoft.AspNetCore.Http.HttpResults;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Endpoints;

internal static class TicketEndpoints
{
    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        // Authorization (who sees/changes what) is enforced inside the services from the CURRENT database state, per role.
        var g = app.MapGroup("/api/v1/tenants/{tenantId:guid}/tickets").WithTags("Tickets").RequireAuthorization(AuthPolicies.TenantMember);

        g.MapPost("/", Create).WithName("CreateTicket");
        g.MapGet("/", List).WithName("ListTickets");
        g.MapGet("/{ticketId:guid}", Get).WithName("GetTicket");
        g.MapPut("/{ticketId:guid}", Update).WithName("UpdateTicket");
        g.MapPost("/{ticketId:guid}/status", Transition).WithName("ChangeTicketStatus");
        g.MapPost("/{ticketId:guid}/reopen", Reopen).WithName("ReopenTicket");
        g.MapGet("/{ticketId:guid}/activity", Activity).WithName("ListTicketActivity");
        g.MapGet("/{ticketId:guid}/sla", Sla).WithName("GetTicketSla");
        g.MapGet("/{ticketId:guid}/assignments", Assignments).WithName("ListTicketAssignments");
        g.MapPost("/{ticketId:guid}/assignments", Assign).WithName("AssignTicketAgents");
        g.MapDelete("/{ticketId:guid}/assignments/{membershipId:guid}", Unassign).WithName("UnassignTicketAgent");
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
