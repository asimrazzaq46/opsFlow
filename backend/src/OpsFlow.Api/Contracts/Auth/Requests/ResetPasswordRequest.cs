using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record ResetPasswordRequest(
    [Required, EmailAddress] string Email,
    [Required] string Token,
    [Required, StringLength(128)] string NewPassword
);
