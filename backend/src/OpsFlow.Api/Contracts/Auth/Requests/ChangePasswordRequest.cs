using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record ChangePasswordRequest(
    [Required, StringLength(128)] string CurrentPassword,
    [Required, StringLength(128)] string NewPassword
);
