using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Organizations;

namespace OpsFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        // UPDATED: custom authentication services (Identity hata diya gaya)
        services.AddScoped<UserTokenService>();
        services.AddScoped<SessionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<ActorAccess>();
        services.AddScoped<MembershipService>();
        services.AddScoped<InvitationService>();
        return services;
    }
}
