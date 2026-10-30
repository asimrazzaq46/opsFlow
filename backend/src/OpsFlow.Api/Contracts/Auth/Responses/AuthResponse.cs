using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

/// <summary>Short-lived access token for the Authorization: Bearer header plus a single-use rotating refresh JWT. Keep both in memory.</summary>
public sealed record AuthResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserInfo User
);
