using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

public sealed record UploadAttachmentRequest([Required] IFormFile File, bool Internal);
