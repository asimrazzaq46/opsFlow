using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Outbox;
using OpsFlow.Application.Tickets;
using OpsFlow.Infrastructure.Background;
using OpsFlow.Infrastructure.Caching;
using OpsFlow.Infrastructure.Email;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Security;
using OpsFlow.Infrastructure.Storage;

namespace OpsFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var connectionString =
            configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<OpsFlowDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IOpsFlowDbContext>(sp => sp.GetRequiredService<OpsFlowDbContext>());
        services.AddScoped<IUserDirectory, UserDirectory>();
        // UPDATED: custom auth primitives (hasher, JWT, per-request session check)
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>(); // UPDATED: access + refresh JWT ki ek service
        services.AddScoped<ISessionValidator, SessionValidator>();

        // UPDATED: attachments - private local storage (provider abstraction: IAttachmentStorage)
        services.Configure<AttachmentOptions>(
            configuration.GetSection(AttachmentOptions.SectionName)
        );
        services.AddSingleton<IAttachmentStorage, LocalAttachmentStorage>();

        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IEmailService, SmtpEmailService>();

        // UPDATED: Milestone 4 - outbox/SLA/background options, jobs and the optional Redis application cache
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.Configure<SlaOptions>(configuration.GetSection(SlaOptions.SectionName));
        services.Configure<BackgroundOptions>(
            configuration.GetSection(BackgroundOptions.SectionName)
        );
        services.AddScoped<OutboxDispatchJob>();
        services.AddScoped<SlaScanJob>();
        services.AddScoped<OutboxMaintenanceJob>();
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        if (
            configuration[
                $"{CacheOptions.SectionName}:{nameof(CacheOptions.RedisConnectionString)}"
            ]
                is { Length: > 0 } redis
            && !string.IsNullOrWhiteSpace(redis)
        )
        {
            // UPDATED: galat Cache:RedisConnectionString startup par saaf error deta hai (production config chhupti nahi); runtime outages phir bhi DB par fallback karte hain
            try
            {
                _ = RedisTenantCache.BuildConfiguration(redis);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "Cache:RedisConnectionString is not a valid Redis connection string (expected e.g. '127.0.0.1:6380' or 'host:6379,password=...,ssl=true').",
                    ex
                );
            }

            services.Replace(ServiceDescriptor.Singleton<ITenantCache, RedisTenantCache>());
        }

        return services;
    }
}
