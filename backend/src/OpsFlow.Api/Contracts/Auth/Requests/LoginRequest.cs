using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, StringLength(128)] string Password
);
