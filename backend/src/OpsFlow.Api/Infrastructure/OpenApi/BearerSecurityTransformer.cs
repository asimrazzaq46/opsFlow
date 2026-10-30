using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OpsFlow.Api.Infrastructure.OpenApi;

/// <summary>Registers the HTTP Bearer scheme (Swagger UI Authorize button) and secures ONLY operations that are not AllowAnonymous.</summary>
internal sealed class BearerSecurityTransformer
    : IOpenApiDocumentTransformer,
        IOpenApiOperationTransformer
{
    private const string Scheme = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT access token from login/refresh. Refresh tokens are rejected here.",
        };
        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(Scheme, context.Document)] = [],
            },
        ];
        ProblemResponsesTransformer.AddProblem(
            operation,
            context.Document,
            "401",
            "Missing, expired or invalid Bearer access token."
        );
        if (
            (context.Description.RelativePath ?? string.Empty).StartsWith(
                "api/v1/tenants/",
                StringComparison.Ordinal
            )
        )
        {
            ProblemResponsesTransformer.AddProblem(
                operation,
                context.Document,
                "403",
                "The caller has no active membership with a sufficient role in this tenant."
            );
        }

        return Task.CompletedTask;
    }
}
