namespace OpsFlow.Api.Infrastructure.Initialization;

public static class DevelopmentInitializationExtensions
{
    /// <summary>Registers the startup check (+ optional auto-apply of existing migrations, see <see cref="DatabaseStartupOptions"/>) and seeders ONLY in Development and only when explicitly enabled.</summary>
    public static IServiceCollection AddDevelopmentInitialization(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<DevelopmentSeedOptions>(configuration.GetSection(DevelopmentSeedOptions.SectionName));
        services.Configure<DatabaseStartupOptions>(configuration.GetSection(DatabaseStartupOptions.SectionName));

        if (environment.IsDevelopment() && configuration.GetValue<bool>($"{DevelopmentInitializationOptions.SectionName}:Enabled"))
        {
            services.AddScoped<IDevelopmentSeeder, DevelopmentAdminSeeder>();
            services.AddScoped<IDevelopmentSeeder, DevelopmentTenantDefaultsSeeder>(); // UPDATED: demo tenant ke ticket defaults
            services.AddHostedService<DevelopmentInitializer>();
        }

        return services;
    }
}
