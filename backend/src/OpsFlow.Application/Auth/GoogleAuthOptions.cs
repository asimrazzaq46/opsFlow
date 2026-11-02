namespace OpsFlow.Application.Auth;

/// <summary>Google Sign-In (Google Identity Services ID-token flow). Only the public client id is needed; no client secret is used.</summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";

    /// <summary>OAuth Web client id. Empty disables Google sign-in without affecting password authentication.</summary>
    public string ClientId { get; set; } = string.Empty;

    public int ChallengeLifetimeSeconds { get; set; } = 300;

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId);
}
