using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets/{ticketId:guid}/tasks")]
[Authorize(Policy = AuthPolicies.TenantStaff)] // Customers are rejected before any task data is touched
[Tags("Ticket tasks")]
public sealed class TicketTasksController(TicketTaskService tasks) : ControllerBase
{
    [HttpGet(Name = "ListTicketTasks")]
    [EndpointSummary("List child tasks")]
    [EndpointDescription(
        "Returns all internal child tasks of a ticket in their defined order, with a status summary and the caller's permissions. Owners and Managers see the tasks of every ticket; an Agent only while assigned to the parent ticket (otherwise 404). Customers are always rejected (403). Tasks are read-only while the ticket is Resolved or Closed."
    )]
    [ProducesResponseType<TicketTaskListDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TicketTaskListDto>> ListTicketTasks(
        Guid ticketId,
        CancellationToken ct
    )
    {
        var result = await tasks.ListAsync(ticketId, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPost(Name = "CreateTicketTask")]
    [EndpointSummary("Create child task")]
    [EndpointDescription(
        "Creates an internal task in status Todo at the end of the list. Owners and Managers must choose assigneeMembershipId: an active Agent currently assigned to the ticket. An Agent assigned to the ticket creates tasks only for themselves (assigneeMembershipId may be omitted; another id is 403). Returns 409 when the ticket is Resolved/Closed or the agent is not assigned to the ticket, 400 for invalid input or an ineligible member. The parent ticket's status, SLA and assignments are never changed."
    )]
    [ProducesResponseType<TicketTaskDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketTaskDto>> CreateTicketTask(
        Guid tenantId,
        Guid ticketId,
        CreateTicketTaskRequest request,
        CancellationToken ct
    )
    {
        var result = await tasks.CreateAsync(
            ticketId,
            new CreateTicketTaskCommand(
                request.Title,
                request.Description,
                request.AssigneeMembershipId
            ),
            ct
        );
        return result.Result is ObjectResult failure
            ? this.Answer(failure)
            : Created($"/api/v1/tenants/{tenantId}/tickets/{ticketId}/tasks", result.Value);
    }

    [HttpPatch("{taskId:guid}", Name = "UpdateTicketTask")]
    [EndpointSummary("Update child task details")]
    [EndpointDescription(
        "Replaces the title and description of a task (a null or empty description clears it). Owners and Managers may edit any task; an Agent only their own task (403 otherwise). The version must match the current task version, otherwise 409; 409 also when the ticket is Resolved/Closed."
    )]
    [ProducesResponseType<TicketTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketTaskDto>> UpdateTicketTask(
        Guid ticketId,
        Guid taskId,
        UpdateTicketTaskRequest request,
        CancellationToken ct
    )
    {
        var result = await tasks.UpdateDetailsAsync(
            ticketId,
            taskId,
            request.Title,
            request.Description,
            request.Version!.Value,
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPatch("{taskId:guid}/status", Name = "ChangeTicketTaskStatus")]
    [EndpointSummary("Change child task status")]
    [EndpointDescription(
        "Moves a task between Todo, InProgress and Done. Done records the completion time; leaving Done clears it. Owners and Managers may change any task; an Agent only their own task (403 otherwise). A stale version or a Resolved/Closed ticket is 409. Completing every task never resolves the parent ticket."
    )]
    [ProducesResponseType<TicketTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketTaskDto>> ChangeTicketTaskStatus(
        Guid ticketId,
        Guid taskId,
        ChangeTicketTaskStatusRequest request,
        CancellationToken ct
    )
    {
        var result = await tasks.ChangeStatusAsync(
            ticketId,
            taskId,
            request.Status!.Value,
            request.Version!.Value,
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPatch("{taskId:guid}/assignee", Name = "ReassignTicketTask")]
    [EndpointSummary("Reassign child task")]
    [EndpointDescription(
        "Changes the responsible agent of a task. Requires the Owner or Manager role (403 otherwise). The new assignee must be an active Agent of the tenant (400) who is currently assigned to the ticket (409). A stale version or a Resolved/Closed ticket is 409. The previous and new assignee are recorded in the internal audit history."
    )]
    [ProducesResponseType<TicketTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketTaskDto>> ReassignTicketTask(
        Guid ticketId,
        Guid taskId,
        ReassignTicketTaskRequest request,
        CancellationToken ct
    )
    {
        var result = await tasks.ReassignAsync(
            ticketId,
            taskId,
            request.AssigneeMembershipId!.Value,
            request.Version!.Value,
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPut("order", Name = "ReorderTicketTasks")]
    [EndpointSummary("Reorder child tasks")]
    [EndpointDescription(
        "Atomically sets the order of ALL tasks of the ticket. Requires the Owner or Manager role (403 otherwise). Every task must appear exactly once with the version the client last saw: duplicates or ids of another ticket are 400; a missing task (created concurrently), a stale version or a Resolved/Closed ticket is 409. Returns the reordered list."
    )]
    [ProducesResponseType<TicketTaskListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TicketTaskListDto>> ReorderTicketTasks(
        Guid ticketId,
        ReorderTicketTasksRequest request,
        CancellationToken ct
    )
    {
        var result = await tasks.ReorderAsync(
            ticketId,
            request.Tasks.ConvertAll(t => new TaskOrderItem(t.TaskId!.Value, t.Version!.Value)),
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }
}
