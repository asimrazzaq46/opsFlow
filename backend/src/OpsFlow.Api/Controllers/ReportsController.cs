using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Application.Analytics;
using OpsFlow.Domain.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/reports")]
[Authorize(Policy = AuthPolicies.TenantManager)]
public sealed class ReportsController(AnalyticsService analytics) : ControllerBase
{
    [HttpGet("export", Name = "ExportReportCsv")]
    [EndpointSummary("Export report as CSV")]
    [EndpointDescription(
        "UTF-8 CSV of the report filtered exactly like the Reports screen (From/To are inclusive days in the organization's reporting timezone). Tickets export is capped at 10,000 rows; the X-Export-Truncated header says when the cap was hit. Requires the Owner or Manager role in the tenant."
    )]
    [Produces("text/csv")]
    [ProducesResponseType<string>(StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> ExportReportCsv(
        CancellationToken ct,
        ReportExportKind kind = ReportExportKind.Tickets,
        DateOnly? from = null,
        DateOnly? to = null,
        string? priority = null,
        Guid? categoryId = null,
        string? status = null,
        Guid? agentMembershipId = null,
        Guid? customerId = null
    )
    {
        if (
            !TryParse<TicketPriority>(priority, nameof(priority), out var parsedPriority)
            || !TryParse<TicketStatus>(status, nameof(status), out var parsedStatus)
        )
        {
            return ValidationProblem(ModelState);
        }

        var csv = await analytics.ExportAsync(
            kind,
            new AnalyticsFilter(
                from,
                to,
                parsedPriority,
                categoryId,
                parsedStatus,
                agentMembershipId,
                customerId
            ),
            ct
        );
        Response.Headers["X-Export-Truncated"] = csv.Truncated ? "true" : "false";
        Response.Headers.CacheControl = "no-store";
        // Rows stream from the database reader straight into the response; the full CSV is never buffered in memory.
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = csv.FileName }.ToString();
        await csv.WriteAsync(Response.Body, ct);
        return new EmptyResult();
    }

    // Strings (not nullable enums) keep the shared TicketStatus/TicketPriority OpenAPI schemas non-nullable for the generated client.
    private bool TryParse<T>(string? value, string field, out T? parsed)
        where T : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result))
        {
            parsed = result;
            return true;
        }

        ModelState.AddModelError(field, $"'{value}' is not a valid value.");
        return false;
    }
}
