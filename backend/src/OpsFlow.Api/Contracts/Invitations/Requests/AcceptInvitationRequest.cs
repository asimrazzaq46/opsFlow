using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record AcceptInvitationRequest([Required, StringLength(200)] string Token);
