using System.Security.Claims;
using OpsFlow.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Infrastructure;

internal sealed class TenantRoleRequirement(IReadOnlyCollection<TenantRole> roles) : IAuthorizationRequirement
{
    public IReadOnlyCollection<TenantRole> Roles { get; } = roles;
}

/// <summary>
/// Resolves the tenant from the {tenantId} route value, validates the caller's ACTIVE membership and role
/// against the database, and only then populates the request's TenantContext. Never trusts client-supplied roles.
/// </summary>
internal sealed class TenantRoleHandler(IOpsFlowDbContext db, TenantContext tenantContext) : AuthorizationHandler<TenantRoleRequirement, HttpContext>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRoleRequirement requirement, HttpContext http)
    {
        if (!Guid.TryParse(http.Request.RouteValues["tenantId"]?.ToString(), out var tenantId)
            || !Guid.TryParse(context.User.FindFirstValue(AuthClaims.Subject), out var userId)) // UPDATED: JWT "sub" claim
        {
            return;
        }

        var membership = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.UserId == userId && m.Status == MembershipStatus.Active)
            .Select(m => new { m.Id, m.Role })
            .SingleOrDefaultAsync(http.RequestAborted);

        if (membership is null || !requirement.Roles.Contains(membership.Role))
        {
            return;
        }

        tenantContext.Set(tenantId, membership.Id, membership.Role);
        context.Succeed(requirement);
    }
}
