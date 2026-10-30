using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Infrastructure.Authentication;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Common;
using OpsFlow.Domain.Tenants;

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
    public Guid UserId => Claim(AuthClaims.Subject);

    public Guid SessionId => Claim(AuthClaims.SessionId);

    private Guid Claim(string type) => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(type), out var id)
        ? id
        : throw AppException.Unauthorized("Authentication is required.");
}

internal static class ApiSecurityExtensions
{
    public const string CorsPolicy = "Spa";

    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        // UPDATED: Identity cookies ki jagah JWT bearer + validated Auth options (fail-fast at host start, not at design time)
        var authOptions = services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));
        var corsOptions = services.AddOptions<CorsSettings>().Bind(configuration.GetSection(CorsSettings.SectionName));
        if (!IsOpenApiDocumentGeneration)
        {
            authOptions.ValidateOnStart();
            corsOptions.ValidateOnStart();
        }

        services.AddSingleton<IValidateOptions<AuthOptions>>(new AuthOptionsValidator(environment));
        services.AddSingleton<IValidateOptions<CorsSettings>>(new CorsSettingsValidator(environment));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.ConfigureOptions<ConfigureJwtBearerOptions>();

        var origins = configuration.GetSection($"{CorsSettings.SectionName}:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
        {
            if (origins.Length == 0)
            {
                p.SetIsOriginAllowed(_ => false);
                return;
            }

            // UPDATED: Bearer-only API hai - credentials/cookies allow nahi kiye jate
            p.WithOrigins(origins).WithMethods("GET", "POST", "PUT", "DELETE").WithHeaders("Authorization", "Content-Type", "X-Requested-With", "X-SignalR-User-Agent"); // UPDATED: SignalR negotiate headers
        }));

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

    // The build-time OpenAPI generator boots the host without any secrets; it must not trip the fail-fast validation.
    // Every real run (and the lazy first-use validation) still enforces the rules.
    private static bool IsOpenApiDocumentGeneration => Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

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
