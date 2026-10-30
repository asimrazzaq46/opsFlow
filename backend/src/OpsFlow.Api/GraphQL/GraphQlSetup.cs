using System.Reflection;
using HotChocolate;
using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;

namespace OpsFlow.Api.GraphQL;

/// <summary>Resolvers share one scoped DbContext, so root fields run one at a time (HotChocolate would otherwise run them in parallel).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SerialAttribute : ObjectFieldDescriptorAttribute
{
    // HotChocolate may resolve from its own DI scope, not the HTTP request scope where TenantRoleHandler validated the membership.
    // Copy exactly that validated result (never client input) into the resolver scope's TenantContext.
    private static void AdoptAuthorizedTenant(IServiceProvider resolverServices)
    {
        var http = resolverServices.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var source = http?.RequestServices.GetRequiredService<TenantContext>();
        var target = resolverServices.GetRequiredService<TenantContext>();
        if (
            source is { TenantId: { } tenant, MembershipId: { } membership, Role: { } role }
            && !ReferenceEquals(source, target)
        )
        {
            target.Set(tenant, membership, role);
        }
    }

    protected override void OnConfigure(
        IDescriptorContext context,
        IObjectFieldDescriptor descriptor,
        MemberInfo? member
    ) =>
        descriptor.Use(next =>
            async ctx =>
            {
                AdoptAuthorizedTenant(ctx.Services);
                var gate = ctx.Services.GetRequiredService<GraphQlGate>();
                await gate.Semaphore.WaitAsync(ctx.RequestAborted);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await next(ctx);
                }
                finally
                {
                    gate.Semaphore.Release();
                    if (watch.ElapsedMilliseconds >= GraphQlLog.SlowFieldMilliseconds) // UPDATED: slow-query warning (sirf field naam + muddat)
                    {
                        GraphQlLog.SlowField(
                            ctx.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OpsFlow.GraphQL"),
                            ctx.Selection.Field.Name,
                            watch.ElapsedMilliseconds,
                            ctx.Services.GetRequiredService<TenantContext>().TenantId
                        );
                    }
                }
            }
        );
}

internal sealed class GraphQlGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>Maps AppException to a stable, safe error code; everything else is masked.</summary>
internal sealed class AppExceptionErrorFilter : IErrorFilter
{
    // UPDATED: pehle unexpected errors sirf mask hote the; ab server-side log bhi hote hain (client ko generic message)
    private static IError Unexpected(IError error)
    {
        GraphQlLog.Unexpected(GraphQlDiagnostics.Logger, error.Exception!, error.Path?.ToString());
        return error.WithMessage("An unexpected error occurred.").WithCode("INTERNAL_ERROR");
    }

    public IError OnError(IError error) =>
        error.Exception switch
        {
            OperationCanceledException => error
                .WithMessage("The request was cancelled.")
                .WithCode("CANCELLED"),
            AppException ex => error
                .WithMessage(ex.Title)
                .WithCode(
                    ex.Code
                        ?? ex.StatusCode switch
                        {
                            400 => "VALIDATION_ERROR",
                            401 => "UNAUTHENTICATED",
                            403 => "FORBIDDEN",
                            404 => "NOT_FOUND",
                            409 => "CONFLICT",
                            _ => "ERROR",
                        }
                ),
            not null => Unexpected(error),
            _ => error,
        };
}

internal static class GraphQlSetup
{
    public const string Prefix = "/api/v1/tenants/{tenantId:guid}";

    public static IServiceCollection AddOpsFlowGraphQl(
        this IServiceCollection services,
        IHostEnvironment env
    )
    {
        services.AddScoped<GraphQlGate>();
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddErrorFilter<AppExceptionErrorFilter>()
            .AddMaxExecutionDepthRule(12)
            // UPDATED: root fields ke asli weights Query.cs mein; ek request ka total field cost 250 se upar reject (dashboard+report+board = 130)
            .ModifyCostOptions(o =>
            {
                o.MaxFieldCost = 250;
                o.MaxTypeCost = 1_000;
                o.EnforceCostLimits = true;
            })
            .ModifyRequestOptions(o =>
            {
                o.ExecutionTimeout = TimeSpan.FromSeconds(20);
                o.IncludeExceptionDetails = false;
            })
            .DisableIntrospection(!env.IsDevelopment());
        return services;
    }

    public static IEndpointRouteBuilder MapOpsFlowGraphQl(
        this IEndpointRouteBuilder app,
        IHostEnvironment env
    )
    {
        GraphQlDiagnostics.Logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("OpsFlow.GraphQL");
        // Same tenant membership policy as REST: TenantRoleHandler reads {tenantId} from the route and fills TenantContext from the DB.
        app.MapGroup(Prefix)
            .MapGraphQL("/graphql")
            .RequireAuthorization(AuthPolicies.TenantMember)
            .WithOptions(o => o.Tool.Enable = env.IsDevelopment());
        return app;
    }
}
