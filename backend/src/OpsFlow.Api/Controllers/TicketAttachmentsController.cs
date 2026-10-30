using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Api.Contracts;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Api.Infrastructure.OpenApi;
using OpsFlow.Application.Tickets;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/tickets/{ticketId:guid}/attachments")]
[Authorize(Policy = AuthPolicies.TenantMember)]
[Tags("Ticket attachments")]
public sealed class TicketAttachmentsController(TicketAttachmentService attachments) : ControllerBase
{
    [HttpGet(Name = "ListTicketAttachments")]
    [EndpointSummary("List ticket attachments")]
    [EndpointDescription("Returns the files attached to a ticket the caller may access. Customers do not see internal-only files. Requires an active membership in the tenant.")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> ListTicketAttachments(Guid ticketId, CancellationToken ct)
    {
        return Ok(await attachments.ListAsync(ticketId, ct));
    }

    [HttpPost(Name = "UploadTicketAttachment")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(12 * 1024 * 1024)] // hard transport cap above the configurable per-file limit (the service enforces the exact limit)
    [EndpointSummary("Upload ticket attachment")]
    [EndpointDescription("Uploads one file (png, jpg, jpeg, gif, pdf, txt, docx, xlsx) to a ticket the caller may access. The type is verified from the file content, the stored name is server-generated and the file is private. Set internal=true for a staff-only file. Files are not scanned for malware. Requires an active membership in the tenant.")]
    [ProducesResponseType<AttachmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<AttachmentDto>> UploadTicketAttachment(Guid tenantId, Guid ticketId, [FromForm] UploadAttachmentRequest request, CancellationToken ct)
    {
        await using var content = request.File.OpenReadStream();
        var created = await attachments.UploadAsync(ticketId, request.File.FileName, content, request.Internal, ct);
        return Created($"/api/v1/tenants/{tenantId}/tickets/{ticketId}/attachments/{created.Id}", created);
    }

    [HttpGet("{attachmentId:guid}", Name = "DownloadTicketAttachment")]
    [EndpointSummary("Download ticket attachment")]
    [EndpointDescription("Streams one attachment as a download after re-checking the caller's current access to the ticket; Customers cannot fetch internal-only files. Requires an active membership in the tenant.")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "application/octet-stream")] // binary body: the generated client reads it as a Blob
    public async Task<IActionResult> DownloadTicketAttachment(Guid ticketId, Guid attachmentId, CancellationToken ct)
    {
        var file = await attachments.DownloadAsync(ticketId, attachmentId, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName); // attachment disposition with a safely encoded file name
    }
}
