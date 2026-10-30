namespace OpsFlow.Api.Infrastructure.Initialization;

public sealed class DevelopmentInitializationOptions
{
    public const string SectionName = "DevelopmentInitialization";

    /// <summary>Master switch; only honoured in the Development environment.</summary>
    public bool Enabled { get; set; }
}

// UPDATED: Development startup par maujooda (already generated) migrations khud apply karne ka explicit switch
public sealed class DatabaseStartupOptions
{
    public const string SectionName = "Database";

    /// <summary>Applies PENDING migrations at startup; honoured ONLY in the Development environment. Never generates migrations.</summary>
    public bool AutoApplyMigrationsOnStartup { get; set; }
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
