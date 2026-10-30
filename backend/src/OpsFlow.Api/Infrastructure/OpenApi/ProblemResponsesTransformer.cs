using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

namespace OpsFlow.Api.Infrastructure.OpenApi;

/// <summary>RFC 9457 problem details exactly as ProblemDetailsSetup emits them. Never contains SQL, stack traces, tokens or internal paths.</summary>
/// <param name="Type">Always about:blank: the stable code is the discriminator.</param>
/// <param name="Title">Short human-readable summary.</param>
/// <param name="Status">HTTP status code.</param>
/// <param name="Detail">Safe explanation of this occurrence.</param>
/// <param name="Code">Stable application error code, e.g. AUTH_INVALID_CREDENTIALS, VALIDATION_ERROR, CONFLICT.</param>
/// <param name="TraceId">Correlates the response with server-side logs.</param>
public record ProblemResponse(string Type, string Title, [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Status, string? Detail, string Code, string TraceId);

/// <summary>Problem details of a 400 response with field-level validation messages.</summary>
/// <param name="Errors">Validation messages keyed by request field.</param>
public sealed record ValidationProblemResponse(string Type, string Title, int Status, string? Detail, string Code, string TraceId, IDictionary<string, string[]> Errors)
    : ProblemResponse(Type, Title, Status, Detail, Code, TraceId);

/// <summary>Shared error documentation: the problem schemas plus the responses every operation of that kind has (400/404/429; 401/403 come from the Bearer transformer).</summary>
internal sealed class ProblemResponsesTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[nameof(ProblemResponse)] = await context.GetOrCreateSchemaAsync(typeof(ProblemResponse), null, cancellationToken);
        document.Components.Schemas[nameof(ValidationProblemResponse)] = await context.GetOrCreateSchemaAsync(typeof(ValidationProblemResponse), null, cancellationToken);
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var hasInput = operation.RequestBody is not null || operation.Parameters?.Any(p => p.In == ParameterLocation.Query) == true;
        if (hasInput)
        {
            AddProblem(operation, context.Document, "400", "Validation failed; see `errors` for the offending fields.", validation: true);
        }

        if (operation.Parameters?.Any(p => p.In == ParameterLocation.Path && p.Name != "tenantId") == true)
        {
            AddProblem(operation, context.Document, "404", "The resource does not exist or is not visible to the caller.");
        }

        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<EnableRateLimitingAttribute>().Any())
        {
            AddProblem(operation, context.Document, "429", "Rate limit exceeded; retry later.");
        }

        // Anonymous endpoints that still answer 401 (bad credentials / bad refresh token).
        if (operation.OperationId is "Login" or "RefreshSession")
        {
            AddProblem(operation, context.Document, "401", "Invalid credentials or refresh token (generic on purpose).");
        }

        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            parameter.Description ??= parameter.Name switch
            {
                "tenantId" => "Tenant (organization) id. Only a request: the server validates the caller's active membership and role before using it.",
                "page" => "1-based page number (clamped to a safe range).",
                "pageSize" => "Items per page (capped by the endpoint).",
                _ => null,
            };
        }

        return Task.CompletedTask;
    }

    public static void AddProblem(OpenApiOperation operation, OpenApiDocument? document, string status, string description, bool validation = false)
    {
        operation.Responses ??= [];
        if (operation.Responses.ContainsKey(status))
        {
            return;
        }

        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(validation ? nameof(ValidationProblemResponse) : nameof(ProblemResponse), document) },
            },
        };
    }
}

internal static class ProblemResponseExtensions
{
    /// <summary>Documents the 409 (duplicate, invalid state or concurrent change) answer of an endpoint.</summary>
    public static RouteHandlerBuilder ProducesConflict(this RouteHandlerBuilder builder) =>
        builder.Produces<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json");
}
