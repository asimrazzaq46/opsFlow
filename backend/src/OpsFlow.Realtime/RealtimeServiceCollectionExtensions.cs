using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpsFlow.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers SignalR, the publisher and (optionally) the Redis backplane. Register <see cref="IRealtimeGroupAuthorizer"/> (and
    /// <see cref="IRealtimeGroupMembership"/> for private groups) yourself; the default authorizer denies every group. Options are validated
    /// at startup. An <see cref="IUserIdProvider"/> the host registered BEFORE this call is kept; otherwise the validated JWT claim is used.
    /// </summary>
    public static IServiceCollection AddRealtime(
        this IServiceCollection services,
        Action<RealtimeOptions>? configure = null
    )
    {
        var options = new RealtimeOptions();
        configure?.Invoke(options);
        services
            .AddOptions<RealtimeOptions>()
            .Configure(o => configure?.Invoke(o))
            .Validate(
                o => o.HubPath.StartsWith('/') && o.HubPath.Length > 1,
                "Realtime:HubPath must start with '/'."
            )
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.UserIdClaim),
                "Realtime:UserIdClaim is required."
            )
            .Validate(
                o => o.MaxGroupNameLength is > 0 and <= 1000,
                "Realtime:MaxGroupNameLength must be between 1 and 1000."
            )
            .ValidateOnStart();

        // UPDATED: AddSignalR ka default IUserIdProvider pehle register hota tha aur hamara provider kabhi nahi chalta tha; ab hamara pehle (TryAdd),
        // is liye jo provider host ne AddRealtime se pehle register kiya ho wo kabhi override nahi hota
        services.TryAddSingleton<IUserIdProvider, ClaimUserIdProvider>();
        var signalR = services
            .AddSignalR()
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter()
                )
            ); // enums travel as strings, like the REST API
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            signalR.AddStackExchangeRedis(options.RedisConnectionString);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RealtimeConnectionRegistry>();
        services.TryAddSingleton<IRealtimeGroupManager, RealtimeGroupManager>();
        services.TryAddSingleton<IRealtimeGroupAuthorizer, DenyAllGroupAuthorizer>();
        services.TryAddScoped<IRealtimePublisher, RealtimePublisher>();
        return services;
    }

    /// <summary>Maps the hub at <see cref="RealtimeOptions.HubPath"/>; the hub itself requires an authenticated user, so authentication must be configured.</summary>
    public static HubEndpointConventionBuilder MapRealtimeHub(this IEndpointRouteBuilder endpoints)
    {
        var services = endpoints.ServiceProvider;
        if (services.GetService<IAuthenticationSchemeProvider>() is null)
        {
            throw new InvalidOperationException(
                "MapRealtimeHub requires authentication: call AddAuthentication()/AddAuthorization() and UseAuthentication()/UseAuthorization()."
            );
        }

        return endpoints.MapHub<RealtimeHub>(
            services.GetRequiredService<IOptions<RealtimeOptions>>().Value.HubPath
        );
    }

    /// <summary>
    /// Browsers cannot set an Authorization header on WebSocket/SSE requests, so SignalR sends <c>access_token</c> in the query string.
    /// Only the hub path may use it. Call from <c>JwtBearerEvents.OnMessageReceived</c> and assign the result to <c>context.Token</c>.
    /// </summary>
    public static string? HubAccessToken(this HttpContext context, string hubPath) =>
        context.Request.Path.StartsWithSegments(hubPath, StringComparison.OrdinalIgnoreCase)
        && context.Request.Query.TryGetValue("access_token", out var token)
            ? token.ToString()
            : null;
}
