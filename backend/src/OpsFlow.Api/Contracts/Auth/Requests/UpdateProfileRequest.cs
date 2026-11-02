using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>Only the display name is editable through the profile endpoint.</summary>
public sealed record UpdateProfileRequest([Required, StringLength(100, MinimumLength = 2)] string DisplayName);
