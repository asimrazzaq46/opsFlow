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

    [HttpPost("{skillId:guid}/assignments", Name = "UpdateSkillAssignments")]
    [EndpointSummary("Assign or remove a skill for several members")]
    [EndpointDescription(
        "Gives one skill to the listed memberships and/or removes it from others in a single all-or-nothing operation; the members' other skills are never touched and members that already match are skipped. Owners may change every staff member, Managers only Agents (403 otherwise); an unknown, cross-tenant or Customer membership is 404. Only active skills can be newly given (400). A member can have at most 30 skills. Each changed member is audited. Requires the Owner or Manager role in the tenant."
    )]
    [ProducesResponseType<SkillAssignmentResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<SkillAssignmentResultDto>> UpdateSkillAssignments(
        Guid skillId,
        UpdateSkillAssignmentsRequest request,
        CancellationToken ct
    )
    {
        var result = await team.UpdateSkillAssignmentsAsync(skillId, request.AddMembershipIds, request.RemoveMembershipIds, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
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
