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
using OpsFlow.Realtime;

namespace OpsFlow.Infrastructure;

public static partial class DependencyInjection
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
        // Custom auth primitives: password hasher, JWT, per-request session check
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>(); // issues and validates both access and refresh JWTs
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<IRealtimeConnectionValidator, RealtimeSessionValidator>(); // same rule, applied to live SignalR connections

        // Google sign-in is optional: without GoogleAuth:ClientId the endpoints answer AUTH_GOOGLE_DISABLED and password auth is unaffected.
        var googleClientId = configuration[$"{GoogleAuthOptions.SectionName}:{nameof(GoogleAuthOptions.ClientId)}"];
        if (
            !string.IsNullOrWhiteSpace(googleClientId)
            && !GoogleClientId().IsMatch(googleClientId)
        )
        {
            throw new InvalidOperationException(
                "GoogleAuth:ClientId must be a Google OAuth web client id ('<number>-<id>.apps.googleusercontent.com'). Leave it empty to disable Google sign-in."
            );
        }

        services.Configure<GoogleAuthOptions>(configuration.GetSection(GoogleAuthOptions.SectionName));
        services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();
        services.AddSingleton<IOneTimeChallengeStore, InMemoryChallengeStore>(); // replaced by Redis below when configured

        // Attachments: private local storage behind the IAttachmentStorage abstraction
        services.Configure<AttachmentOptions>(
            configuration.GetSection(AttachmentOptions.SectionName)
        );
        services.AddSingleton<IAttachmentStorage, LocalAttachmentStorage>();

        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IEmailService, SmtpEmailService>();

        // Outbox/SLA/background options, jobs and the optional Redis application cache
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
            // An invalid Cache:RedisConnectionString fails clearly at startup so a broken production config is not hidden; runtime outages still fall back to the database.
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
            services.Replace(ServiceDescriptor.Singleton<IOneTimeChallengeStore, RedisChallengeStore>());
        }

        return services;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$")]
    private static partial System.Text.RegularExpressions.Regex GoogleClientId();
}
