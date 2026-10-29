using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Api.Infrastructure;

internal static class RateLimitPolicies
{
    public const string Sensitive = "sensitive";
}

internal static class AuthPolicies
{
    public const string TenantMember = nameof(TenantMember);
    public const string TenantStaff = nameof(TenantStaff);
    public const string TenantManager = nameof(TenantManager);
    public const string TenantOwner = nameof(TenantOwner);
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : throw AppException.Unauthorized("Authentication is required.");
}

internal static class ApiSecurityExtensions
{
    private const string AntiforgeryHeader = "X-XSRF-TOKEN";

    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var securePolicy = environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.SignIn.RequireConfirmedEmail = true;
                o.Password.RequiredLength = 10;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<OpsFlowDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // UPDATED: stamp har request par verify hota hai (default 30 minute ka gap sessions ko zinda rakhta tha)
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "opsflow.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = securePolicy;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            // UPDATED: har request par security stamp + lockout check, taake password reset/lockout par purani sessions foran band hon
            o.Events.OnValidatePrincipal = async ctx =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(ctx);
                if (ctx.Principal is null)
                {
                    return;
                }

                var users = ctx.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.GetUserAsync(ctx.Principal);
                if (user is null || await users.IsLockedOutAsync(user))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };
            // API clients get status codes, never login redirects.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AntiforgeryHeader;
            o.Cookie.Name = "opsflow.csrf";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = securePolicy;
        });

        services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthPolicies.TenantMember, TenantPolicy(TenantRole.TenantOwner, TenantRole.Manager, TenantRole.Agent, TenantRole.Customer))
            .AddPolicy(AuthPolicies.TenantStaff, TenantPolicy(TenantRole.TenantOwner, TenantRole.Manager, TenantRole.Agent))
            .AddPolicy(AuthPolicies.TenantManager, TenantPolicy(TenantRole.TenantOwner, TenantRole.Manager))
            .AddPolicy(AuthPolicies.TenantOwner, TenantPolicy(TenantRole.TenantOwner));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                Window("global:", ctx, configuration.GetValue("RateLimiting:GlobalPermitLimit", 300)));
            o.AddPolicy(RateLimitPolicies.Sensitive, ctx =>
                Window("sensitive:", ctx, configuration.GetValue("RateLimiting:SensitivePermitLimit", 10)));
            o.OnRejected = async (ctx, _) => await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = ctx.HttpContext,
                ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests." },
            });
        });
        return services;
    }

    private static Action<AuthorizationPolicyBuilder> TenantPolicy(params TenantRole[] roles) =>
        p => p.RequireAuthenticatedUser().AddRequirements(new TenantRoleRequirement(roles));

    // shortcut: partitioned by remote IP only; behind a reverse proxy configure ForwardedHeaders first.
    private static RateLimitPartition<string> Window(string prefix, HttpContext ctx, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(prefix + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
