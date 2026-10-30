using System.ComponentModel.DataAnnotations;
using OpsFlow.Domain.Tenants;

namespace OpsFlow.Api.Contracts;

/// <summary>The refresh token travels in the HTTPS JSON body (no cookies are used anywhere).</summary>
public sealed record RefreshRequest([Required, StringLength(2048)] string RefreshToken);
