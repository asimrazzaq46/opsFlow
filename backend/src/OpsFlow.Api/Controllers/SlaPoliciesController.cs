using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/sla-policies")]
public sealed class SlaPoliciesController(SlaPolicyService policies) : ControllerBase
{
    [HttpGet(Name = "ListSlaPolicies")]
    [Authorize(Policy = AuthPolicies.TenantStaff)]
    [EndpointSummary("List SLA policies")]
    [EndpointDescription(
        "Returns the tenant's SLA policies (calendar minutes). Requires an Owner, Manager or Agent role in the tenant."
    )]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> ListSlaPolicies(
        CancellationToken ct
    )
    {
        var result = await policies.ListAsync(ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPost(Name = "CreateSlaPolicy")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Create SLA policy")]
    [EndpointDescription(
        "Creates the active policy of a priority; each priority has exactly one active policy. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<SlaPolicyDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<SlaPolicyDto>> CreateSlaPolicy(
        Guid tenantId,
        CreateSlaPolicyRequest request,
        CancellationToken ct
    )
    {
        var result = await policies.CreateAsync(
            request.Priority,
            request.ResponseMinutes,
            request.ResolutionMinutes,
            ct
        );
        if (result.Result is ObjectResult failure)
        {
            return this.Answer(failure);
        }

        var policy = result.Value!;
        return Created($"/api/v1/tenants/{tenantId}/sla-policies/{policy.Id}", policy);
    }

    [HttpPut("{policyId:guid}", Name = "UpdateSlaPolicy")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Update SLA policy targets")]
    [EndpointDescription(
        "Changes response/resolution targets for future SLA cycles; existing cycles keep their snapshot. Requires the Owner or Manager role in the tenant."
    )]
    public async Task<ActionResult<SlaPolicyDto>> UpdateSlaPolicy(
        Guid policyId,
        UpdateSlaPolicyRequest request,
        CancellationToken ct
    )
    {
        var result = await policies.UpdateAsync(
            policyId,
            request.ResponseMinutes,
            request.ResolutionMinutes,
            ct
        );
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPut("{policyId:guid}/status", Name = "SetSlaPolicyStatus")]
    [Authorize(Policy = AuthPolicies.TenantManager)]
    [EndpointSummary("Activate SLA policy")]
    [EndpointDescription(
        "A priority must always keep an active policy, so an active policy cannot be deactivated (409): edit its targets instead. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<IActionResult> SetSlaPolicyStatus(
        Guid policyId,
        SetStatusRequest request,
        CancellationToken ct
    )
    {
        return await policies.SetActiveAsync(policyId, request.IsActive, ct) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }
}
