using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OpsFlow.Api.Infrastructure.OpenApi;

/// <summary>Built-in ASP.NET Core OpenAPI. Per-endpoint text lives next to the endpoints (WithSummary/WithDescription); the transformers add what is the same everywhere.</summary>
internal static class OpenApiSetup
{
    public static IServiceCollection AddOpsFlowOpenApi(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "OpsFlow API",
                Version = "v1",
                Description = "Multi-tenant service-operations API. Authenticate with POST /api/v1/auth/login, press Authorize and paste the accessToken (without the word Bearer). "
                    + "Access tokens last 15 minutes; renew them with POST /api/v1/auth/refresh. Tenant APIs live under /api/v1/tenants/{tenantId}: the tenant id in the route is only a request; access is decided from the caller's active membership in the database. "
                    + "Errors are RFC 9457 problem details with a stable `code` and a `traceId`.",
            };
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer<BearerSecurityTransformer>();
        options.AddOperationTransformer<BearerSecurityTransformer>();
        options.AddDocumentTransformer<ProblemResponsesTransformer>();
        options.AddOperationTransformer<ProblemResponsesTransformer>();
    });
}
