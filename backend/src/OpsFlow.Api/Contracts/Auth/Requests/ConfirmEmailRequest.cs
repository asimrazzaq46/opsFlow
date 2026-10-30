using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record ConfirmEmailRequest([Required] Guid UserId, [Required] string Token);
