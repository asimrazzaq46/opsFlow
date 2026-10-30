# OpsFlow.Realtime

Reusable, host-agnostic SignalR infrastructure for ASP.NET Core. No dependency on OpsFlow entities, EF Core or any database.

## What it gives you

| Piece | Purpose |
|---|---|
| `RealtimeHub` (`/hubs/realtime`) | `[Authorize]` hub; identity = the validated JWT claim (`sub` by default). Connection ends when its token expires, so clients reconnect with a fresh, re-validated token. |
| `IRealtimePublisher` | `NotifyUserAsync`, `NotifyUsersAsync`, `SendToUsersAsync`, `PublishToGroupMembersAsync` (private groups), `PublishGroupAsync` (non-sensitive groups). Events are records tagged `[RealtimeEvent("name")]`. |
| `IRealtimeGroupAuthorizer` | HOST decides who may join a group (default: deny all). Re-checked on every join. |
| `IRealtimeGroupMembership` | HOST returns the CURRENT members of a private group; used at publish time so stale connections receive nothing. |
| `IRealtimeGroupManager` | `RevokeAsync(user, group)` / `RevokeUserAsync(user)`: evict live connections of this instance when access is removed. |
| Chat pipeline | `ChatService<TMessage>`: validate -> authorize -> host persists -> publish the host's per-audience deliveries. `ChatUser` is the trusted identity. |
| Redis backplane | Optional (`Realtime:RedisConnectionString`), scale-out only. Never a message store. |

Publisher semantics: invalid input (missing `[RealtimeEvent]`, empty ids, null payload, no membership provider) throws; a transport failure is logged and swallowed (post-commit best effort); a cancelled token throws `OperationCanceledException`.

## Setup

```csharp
builder.Services.AddRealtime(o => builder.Configuration.GetSection("Realtime").Bind(o)); // validated at startup
builder.Services.AddScoped<IRealtimeGroupAuthorizer, MyGroupAuthorizer>();   // optional
builder.Services.AddScoped<IRealtimeGroupMembership, MyGroupMembership>();   // needed for private groups

// JwtBearerEvents.OnMessageReceived (WebSockets/SSE cannot send an Authorization header):
if (string.IsNullOrEmpty(context.Token)) context.Token = context.HttpContext.HubAccessToken("/hubs/realtime");

app.MapRealtimeHub();   // requires AddAuthentication()/UseAuthentication()
```

A host `IUserIdProvider` registered BEFORE `AddRealtime` is kept; otherwise the JWT claim is used.

## Examples

**1. Individual notification**
```csharp
[RealtimeEvent("invoice.paid")] public sealed record InvoicePaid(Guid InvoiceId);
await publisher.NotifyUserAsync(userId, new InvoicePaid(id));            // after your commit
```

**2. Group notification (private group)** - membership is read from YOUR source of truth at publish time:
```csharp
await publisher.PublishToGroupMembersAsync($"project:{projectId}", new TaskChanged(taskId));
// access removed? also evict the user's live connections right away:
await groupManager.RevokeAsync(userId, $"project:{projectId}");
```
Clients call `JoinGroup("project:42")`; your `IRealtimeGroupAuthorizer` answers from current permissions. Use `PublishGroupAsync` only for public, non-sensitive groups (it reaches every connection that ever joined).

**3. One-to-one chat** - register once, then call from your endpoint:
```csharp
services.AddScoped<IChatConversationAuthorizer, DmAuthorizer>();      // is this user one of the two participants?
services.AddScoped<IChatMessageStore<DmDto>, DmStore>();               // persists, idempotent on ClientMessageId, returns deliveries
services.AddSingleton<IChatMessageValidator>(new DefaultChatMessageValidator(2000));
services.AddScoped<ChatService<DmDto>>();

var sender = ChatUser.FromPrincipal(User);                             // authenticated principal only; never a body value
var result = await chat.SendAsync(sender, conversationId, request.ClientMessageId, request.Content, ct);
```
Your store builds `ChatDelivery` objects (one per audience); each carries a host-defined, already-filtered event:
```csharp
return new ChatAppendResult<DmDto>(dtoForSender, duplicate: false, [new ChatDelivery([peerId], new DmReceived(dtoForPeer))]);
```

**4. Private group chat** - same pipeline; the authorizer checks group membership and the store returns the member ids (read AFTER the commit) as the delivery recipients. Different roles can get different payloads by returning several deliveries.

**5. Optional chatbot / AI response events** - persist the bot reply with the same store, then publish incremental events yourself:
```csharp
[RealtimeEvent("bot.token")] public sealed record BotToken(string ConversationId, string Text);
await foreach (var chunk in model.StreamAsync(prompt, ct))
    await publisher.NotifyUserAsync(userId, new BotToken(conversationId, chunk), ct);   // ephemeral; the final message is stored and recoverable via history
```

## Host responsibilities

- Authentication (JWT validation, session/revocation checks) and the token lifetime policy.
- ALL authorization: group join rules, current group membership, conversation access. The library never trusts client-supplied user/tenant/group ids.
- Persistence: messages, idempotency (`ClientMessageId`), ordering, history endpoints, notification rows, retention.
- Choosing recipients and payload visibility per audience; calling `RevokeAsync` when access is removed.
- Recovery after a reconnect: clients re-fetch history from your API (nothing is replayed).

## Not included

Message storage/outbox, delivery or read receipts, typing/presence, push/email fallbacks, rate limiting, moderation, attachments, multi-instance group eviction (the connection registry is per instance; use `PublishToGroupMembersAsync` and token-bound lifetimes there), Redis availability handling beyond SignalR's own.

## Known limits

- A session revoked at the host keeps an already open socket until its access token expires (the connection is aborted then and cannot reconnect). Private events are still only sent to recipients authorized at publish time.
- The Redis backplane is optional and not exercised by the automated tests; local development runs fully in-process.

## Angular

`frontend/src/app/core/realtime/README.md`: `start`, `on<T>`, `joinGroup` (re-joined after every reconnect), `restart` after token refresh, `stop` on logout.
