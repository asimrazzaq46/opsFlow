using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet(Name = "GetHealth")]
    [AllowAnonymous]
    [EndpointSummary("Check service health")]
    [EndpointDescription("Anonymous liveness probe; returns the service status and server time.")]
    public ActionResult<HealthResponse> GetHealth()
    {
        return Ok(new HealthResponse("Healthy", DateTimeOffset.UtcNow));
    }
}

public sealed record HealthResponse(string Status, DateTimeOffset Timestamp);
