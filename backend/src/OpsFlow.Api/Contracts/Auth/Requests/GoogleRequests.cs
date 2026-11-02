using System.ComponentModel.DataAnnotations;

namespace OpsFlow.Api.Contracts;

/// <summary>The Google ID token from the Google Identity Services callback plus the id of the challenge whose nonce it carries.</summary>
public sealed record GoogleCredentialRequest(
    [Required, StringLength(8192)] string Credential,
    [Required, StringLength(128)] string ChallengeId
);

/// <summary>Password re-authentication that precedes linking a Google account.</summary>
public sealed record GoogleLinkChallengeRequest([Required, StringLength(128)] string Password);
