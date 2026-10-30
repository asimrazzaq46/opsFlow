using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

public sealed record CreateNoteRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Content,
    [Required] Guid? ClientNoteId
);
