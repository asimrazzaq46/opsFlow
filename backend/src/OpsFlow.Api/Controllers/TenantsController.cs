using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Organizations;
using OpsFlow.Application.Tenants;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}")]
public sealed class TenantsController(
    OrganizationService organizations,
    MembershipService members,
    TeamService team,
    TenantDefaultsService defaults,
    TenantSettingsService settings
) : ControllerBase
{
    [HttpGet(Name = "GetTenant")]
    [Authorize(Policy = AuthPolicies.TenantMember)]
    [EndpointSummary("Get organization")]
    [EndpointDescription(
        "Returns the tenant identified by the route and the caller's role in it. Requires an active membership in the tenant; Customers only see their own tickets."
    )]
    public async Task<ActionResult<OrganizationDto>> GetTenant(CancellationToken ct)
    {
        var organization = await organizations.GetCurrentAsync(ct);
        return organization is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Organization not found.")
            : Ok(organization);
    }

    [HttpGet("members", Name = "ListMembers")]
    [Authorize(Policy = AuthPolicies.TenantStaff)]
    [EndpointSummary("List members")]
    [EndpointDescription(
        "Returns the tenant's memberships, paged (pageSize is capped). Requires an Owner, Manager or Agent role in the tenant."
    )]
    public async Task<ActionResult<PagedResult<MemberDto>>> ListMembers(
        CancellationToken ct,
        int page = 1,
        int pageSize = 25
    )
    {
        return Ok(await members.ListAsync(page, pageSize, ct));
    }

    [HttpPut("members/{membershipId:guid}/role", Name = "ChangeMemberRole")]
    [Authorize(Policy = AuthPolicies.TenantOwner)]
    [EndpointSummary("Change member role")]
    [EndpointDescription(
        "Changes a staff member's role in this workspace only. A tenant always keeps at least one usable Owner. An active Agent who still holds open ticket assignments or unfinished tasks cannot leave the Agent role (409 MEMBER_HAS_OPEN_WORK, listing the work); a stale expectedRole is refused (409 MEMBER_CHANGED). The change is audited and the member is told to reload. Requires the Owner role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<IActionResult> ChangeMemberRole(
        Guid membershipId,
        ChangeRoleRequest request,
        CancellationToken ct
    )
    {
        return await members.ChangeRoleAsync(membershipId, request.Role, ct, request.ExpectedRole) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }

    [HttpPut("members/{membershipId:guid}/status", Name = "SetMemberStatus")]
    [Authorize(Policy = AuthPolicies.TenantOwner)]
    [EndpointSummary("Activate or suspend member")]
    [EndpointDescription(
        "Activates or suspends a staff membership in this workspace only. A tenant always keeps at least one usable Owner. Suspending an active Agent who holds open work is refused once with 409 MEMBER_HAS_OPEN_WORK listing that work (canAcknowledge = true); repeating with acknowledgeOpenWork suspends immediately and the work shows as unstaffed for reassignment. A stale expectedRole is refused (409 MEMBER_CHANGED). The change is audited and the member is told to reload. Requires the Owner role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<IActionResult> SetMemberStatus(
        Guid membershipId,
        SetMemberStatusRequest request,
        CancellationToken ct
    )
    {
        return await members.SetActiveAsync(membershipId, request.IsActive, ct, request.ExpectedRole, request.AcknowledgeOpenWork) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }

    [HttpPut("members/{membershipId:guid}/profile", Name = "UpdateMemberProfile")]
    [Authorize(Policy = AuthPolicies.TenantStaff)]
    [EndpointSummary("Update member profile")]
    [EndpointDescription(
        "Sets a staff member's workspace profile (job title, department, phone). Owners may edit every staff member, Managers only Agents; Agents cannot edit profiles, not even their own. The change is audited without copying the values. Requires an Owner or Manager role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateMemberProfile(
        Guid membershipId,
        UpdateMemberProfileRequest request,
        CancellationToken ct
    )
    {
        return await team.UpdateProfileAsync(
            membershipId,
            new UpdateMemberProfileCommand(
                request.JobTitle,
                request.Department,
                request.Phone,
                request.CheckVersion,
                request.ExpectedUpdatedAtUtc
            ),
            ct
        ) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }

    [HttpPut("members/{membershipId:guid}/skills", Name = "SetMemberSkills")]
    [Authorize(Policy = AuthPolicies.TenantStaff)]
    [EndpointSummary("Set member skills")]
    [EndpointDescription(
        "Replaces a staff member's skill tags with the given set; only active skills can be newly added. Owners may edit every staff member, Managers only Agents; Agents cannot edit skills, not even their own. The change is audited. Requires an Owner or Manager role in the tenant."
    )]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> SetMemberSkills(
        Guid membershipId,
        SetMemberSkillsRequest request,
        CancellationToken ct
    )
    {
        return await team.SetMemberSkillsAsync(membershipId, request.SkillIds!, request.ExpectedSkillIds, ct) is { } failure
            ? this.Answer(failure)
            : NoContent();
    }

    [HttpPost("defaults", Name = "InitializeTenantDefaults")]
    [Authorize(Policy = AuthPolicies.TenantOwner)]
    [EndpointSummary("Initialize tenant defaults")]
    [EndpointDescription(
        "Idempotently adds the missing default ticket settings, counter, categories and SLA policies (for tenants created before these defaults were provisioned at onboarding). Requires the Owner role in the tenant."
    )]
    public async Task<ActionResult<DefaultsResult>> InitializeTenantDefaults(CancellationToken ct)
    {
        return Ok(new DefaultsResult(await defaults.EnsureAsync(ct)));
    }

    // The tenant reporting timezone is authoritative; the browser timezone is only a UI suggestion.
    [HttpGet("settings", Name = "GetTenantSettings")]
    [Authorize(Policy = AuthPolicies.TenantStaff)]
    [EndpointSummary("Get organization settings")]
    [EndpointDescription(
        "Returns the effective organization settings: name, read-only slug and ticket number prefix, and the reporting timezone (IANA id, UTC for organizations that never set one). Requires an Owner, Manager or Agent role in the tenant."
    )]
    public async Task<ActionResult<TenantSettingsDto>> GetTenantSettings(CancellationToken ct)
    {
        var result = await settings.GetAsync(ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }

    [HttpPut("settings", Name = "UpdateTenantSettings")]
    [Authorize(Policy = AuthPolicies.TenantOwner)]
    [EndpointSummary("Update organization settings")]
    [EndpointDescription(
        "Sets the reporting timezone used for day-based dashboards, reports and CSV exports and, optionally, renames the organization (name: 2-100 characters; omit or null to keep it). The timezone must be an exact IANA id such as Europe/Rome; a timezone detected by the browser is only a suggestion until an Owner saves it here. The slug, ticket number prefix, categories, SLA policies and existing SLA deadlines are never changed. The tenant always comes from the route. Requires the Owner role in the tenant; 409 when ownership changed concurrently."
    )]
    [ProducesResponseType<TenantSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(
        StatusCodes.Status409Conflict,
        "application/problem+json"
    )]
    public async Task<ActionResult<TenantSettingsDto>> UpdateTenantSettings(
        UpdateTenantSettingsRequest request,
        CancellationToken ct
    )
    {
        var result = await settings.UpdateAsync(request.ReportingTimeZone, request.Name, ct);
        return result.Result is ObjectResult failure ? this.Answer(failure) : Ok(result.Value);
    }
}
