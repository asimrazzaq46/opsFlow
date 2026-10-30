using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Tenants;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Application.Organizations;

public sealed record OrganizationDto(Guid Id, string Name, string Slug, TenantRole Role);
