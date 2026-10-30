using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

public sealed record SessionResponse(
    Guid Id,
    string? UserAgent,
    DateTime CreatedAtUtc,
    DateTime LastUsedAtUtc,
    bool IsCurrent
);
