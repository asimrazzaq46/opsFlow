using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

public sealed record PreviewInvitationRequest([Required, StringLength(200)] string Token);
