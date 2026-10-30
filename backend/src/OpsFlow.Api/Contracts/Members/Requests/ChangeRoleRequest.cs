using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record ChangeRoleRequest([EnumDataType(typeof(TenantRole))] TenantRole Role);
