using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record EmailRequest([Required, EmailAddress, StringLength(256)] string Email);
