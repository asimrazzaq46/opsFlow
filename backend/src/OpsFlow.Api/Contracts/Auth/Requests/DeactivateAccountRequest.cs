using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record DeactivateAccountRequest([Required, StringLength(128)] string Password);
