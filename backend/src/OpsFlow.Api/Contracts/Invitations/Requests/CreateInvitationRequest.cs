using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record CreateInvitationRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [EnumDataType(typeof(TenantRole))] TenantRole Role
);
