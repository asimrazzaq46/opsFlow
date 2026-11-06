using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Memberships;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/skills")]
[Authorize(Policy = AuthPolicies.TenantManager)] // the service re-checks the CURRENT role inside its transaction
[Tags("Team")]
public sealed class SkillsController(TeamService team) : ControllerBase
{
    [HttpPost(Name = "CreateSkill")]
    [EndpointSummary("Create skill")]
    [EndpointDescription(
        "Adds a skill tag with a workspace-unique name (case-insensitive). The change is audited. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<SkillDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<SkillDto>> CreateSkill(Guid tenantId, CreateSkillRequest request, CancellationToken ct)
    {
        var result = await team.CreateSkillAsync(request.Name, ct);
        if (result.Result is ObjectResult failure)
        {
            return this.Answer(failure);
        }

        var skill = result.Value!;
        return Created($"/api/v1/tenants/{tenantId}/skills/{skill.Id}", skill);
    }

    [HttpPut("{skillId:guid}", Name = "UpdateSkill")]
    [EndpointSummary("Rename or deactivate skill")]
    [EndpointDescription(
        "Renames a skill or changes whether it can be newly assigned; members keep a deactivated skill. The change is audited. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<SkillDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<SkillDto>> UpdateSkill(Guid skillId, UpdateSkillRequest request, CancellationToken ct)
    {
        var result = await team.UpdateSkillAsync(skillId, request.Name, request.IsActive, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }
}
