using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record LogoutRequest([Required, StringLength(2048)] string RefreshToken);
