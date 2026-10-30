namespace OpsFlow.Api.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/health")
            .WithTags("Health")
            .MapGet("/", () => TypedResults.Ok(new HealthResponse("Healthy", DateTimeOffset.UtcNow)))
            .WithName("GetHealth")
                .WithSummary("Check service health")
                .WithDescription("Anonymous liveness probe; returns the service status and server time.")
            .AllowAnonymous(); // UPDATED: fallback policy ke baad health endpoint public rehna chahiye
        return app;
    }

    internal sealed record HealthResponse(string Status, DateTimeOffset Timestamp);
}
