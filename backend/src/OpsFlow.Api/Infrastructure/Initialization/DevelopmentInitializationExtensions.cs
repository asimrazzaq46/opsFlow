namespace OpsFlow.Api.Infrastructure.Initialization;

internal static class DevelopmentInitializationExtensions
{
    /// <summary>Registers migrate + seed on startup ONLY in Development and only when explicitly enabled.</summary>
    public static IServiceCollection AddDevelopmentInitialization(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<DevelopmentSeedOptions>(configuration.GetSection(DevelopmentSeedOptions.SectionName));

        if (environment.IsDevelopment() && configuration.GetValue<bool>($"{DevelopmentInitializationOptions.SectionName}:Enabled"))
        {
            services.AddScoped<IDevelopmentSeeder, DevelopmentAdminSeeder>();
            services.AddHostedService<DevelopmentInitializer>();
        }

        return services;
    }
}
