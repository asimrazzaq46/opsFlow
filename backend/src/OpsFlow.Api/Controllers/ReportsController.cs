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
    public async Task ExportReportCsv(
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
        var csv = await analytics.ExportAsync(
            kind,
            new AnalyticsFilter(
                from,
                to,
                Parse<TicketPriority>(priority, nameof(priority)),
                categoryId,
                Parse<TicketStatus>(status, nameof(status)),
                agentMembershipId,
                customerId
            ),
            ct
        );
        Response.Headers["X-Export-Truncated"] = csv.Truncated ? "true" : "false";
        Response.Headers.CacheControl = "no-store";
        // UPDATED: rows database reader se seedha response mein stream hoti hain (poora CSV memory mein nahi banta)
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = csv.FileName }.ToString();
        await csv.WriteAsync(Response.Body, ct);
    }

    // Strings (not nullable enums) keep the shared TicketStatus/TicketPriority OpenAPI schemas non-nullable for the generated client.
    private static T? Parse<T>(string? value, string field)
        where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null
        : Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
        : throw OpsFlow.Application.Common.AppException.Validation(
            field,
            $"'{value}' is not a valid value."
        );
}
