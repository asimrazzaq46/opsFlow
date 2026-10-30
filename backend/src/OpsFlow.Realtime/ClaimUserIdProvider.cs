using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace OpsFlow.Realtime;

/// <summary>User identity = the validated JWT claim only; nothing the client sends can change it.</summary>
internal sealed class ClaimUserIdProvider(IOptions<RealtimeOptions> options) : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst(options.Value.UserIdClaim)?.Value;
}
