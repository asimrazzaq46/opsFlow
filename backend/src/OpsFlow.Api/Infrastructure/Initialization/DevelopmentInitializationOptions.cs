namespace OpsFlow.Api.Infrastructure.Initialization;

public sealed class DevelopmentInitializationOptions
{
    public const string SectionName = "DevelopmentInitialization";

    /// <summary>Master switch; only honoured in the Development environment.</summary>
    public bool Enabled { get; set; }
}

public sealed class DevelopmentSeedOptions
{
    public const string SectionName = "DevelopmentSeed";

    public string AdminEmail { get; set; } = "admin@gmail.com";
    public string AdminDisplayName { get; set; } = "OpsFlow Admin";
    public string OrganizationName { get; set; } = "OpsFlow Demo";

    /// <summary>Never stored in appsettings: supply via user-secrets or an ignored environment variable.</summary>
    public string? AdminPassword { get; set; }
}
