using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Common;
using OpsFlow.Application.Memberships;
using OpsFlow.Domain.Common;
using OpsFlow.Domain.Customers;
using OpsFlow.Domain.Tenants;
using OpsFlow.Domain.Users;

namespace OpsFlow.Application.Customers;

public sealed record CustomerDto(
    Guid Id,
    string Name,
    string Email,
    string? Phone,
    Guid? LinkedUserId,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string? LinkedUserEmail = null,
    string? LinkedUserName = null
);

/// <summary>An active Customer member of the tenant who can be linked to a customer contact.</summary>
public sealed record LinkableUserDto(Guid UserId, string Email, string DisplayName);
