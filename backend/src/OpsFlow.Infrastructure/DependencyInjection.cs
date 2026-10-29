using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Infrastructure.Security;
using OpsFlow.Infrastructure.Email;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<OpsFlowDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IOpsFlowDbContext>(sp => sp.GetRequiredService<OpsFlowDbContext>());
        services.AddScoped<IUserDirectory, UserDirectory>();
        // UPDATED: custom auth primitives (hasher, JWT, per-request session check)
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        services.AddScoped<ISessionValidator, SessionValidator>();

        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IEmailService, SmtpEmailService>();
        return services;
    }
}
