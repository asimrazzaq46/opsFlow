namespace OpsFlow.Api.Infrastructure.Initialization;

public static class DevelopmentInitializationExtensions
{
    /// <summary>Registers the verify-only startup check + seeders ONLY in Development and only when explicitly enabled (no auto-migration anywhere).</summary>
    public static IServiceCollection AddDevelopmentInitialization(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<DevelopmentSeedOptions>(configuration.GetSection(DevelopmentSeedOptions.SectionName));

        if (environment.IsDevelopment() && configuration.GetValue<bool>($"{DevelopmentInitializationOptions.SectionName}:Enabled"))
        {
            services.AddScoped<IDevelopmentSeeder, DevelopmentAdminSeeder>();
            services.AddScoped<IDevelopmentSeeder, DevelopmentTenantDefaultsSeeder>(); // UPDATED: demo tenant ke ticket defaults
            services.AddHostedService<DevelopmentInitializer>();
        }

        return services;
    }
}
