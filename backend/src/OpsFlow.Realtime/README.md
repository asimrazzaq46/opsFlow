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
| `IRealtimeConnectionValidator` | HOST confirms, in batches, which live connections are still entitled to be connected (session revoked? account disabled?). Optional; see "Live connection revalidation". |
| `IRealtimeConnectionRevalidation` | `RequestAsync()` after a revocation commits: revalidate now, on this instance and (with Redis) on every instance. Best effort, never throws. |

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

## Live connection revalidation

Authentication happens once, when a connection is opened, so a revoked session would otherwise keep receiving events until its token expires.
Register a validator and the library keeps checking the connections it holds:

```csharp
services.AddScoped<IRealtimeConnectionValidator, MyValidator>();   // may use a DbContext; one call per batch, never per connection

// after your revocation COMMITTED:
await revalidation.RequestAsync();                                  // optional fast path
```

```csharp
public Task<IReadOnlySet<string>> FindValidAsync(IReadOnlyList<RealtimeConnectionInfo> connections, CancellationToken ct)
// connections carry the principal the connection authenticated with - never client-supplied values.
// Return the ids that are valid. Every id you do not return is closed (fail closed). THROW if your source of truth is unreachable;
// never return "everything valid" as a fallback.
```

| Option | Default | Meaning |
|---|---|---|
| `RevalidationInterval` | 30 s | Periodic check of every connection of this instance. |
| `MaxUnverifiedAge` | 90 s | A connection that could not be verified for this long is closed, whatever the reason (validator down, timing out, throwing). At least 2x the interval. |
| `ValidationTimeout` | 10 s | Upper bound for one validator call; a hung dependency counts as a failed check. |
| `ValidationBatchSize` | 500 | Connections per validator call. |

How it behaves:

- Each instance checks only its own connections against the host's source of truth, so instances need no coordination. Without a validator the monitor stays dormant and connections end at token expiry only.
- One failed or slow check never closes a valid connection; repeated failures close it once `MaxUnverifiedAge` passes. The validation time is taken before the call, so a revocation that lands during a check is never covered by it.
- `RequestAsync()` wakes the monitor at once; bursts coalesce, and a request that arrives during a pass triggers the next pass immediately. With `Realtime:RedisConnectionString` it also publishes an identity-free "revalidate" signal on its own Redis channel (the backplane's configuration and channel prefix, a distinct channel name, one extra Redis connection per instance). A lost signal or a Redis outage only delays the close until the next periodic check.
- Closing uses `HubCallerContext.Abort()`, which is safe from the monitor's thread and idempotent. The client sees a normal close, the hub's disconnect callback removes the registry entry, and entries whose connection already ended are pruned on every pass.

Timing (healthy system, no strict deadline): fast path = one validator batch (milliseconds); periodic fallback <= `RevalidationInterval` + one batch; validator outage <= `MaxUnverifiedAge` + `RevalidationInterval`. A host database outage or a stalled monitor delays enforcement; watch the "Realtime revalidation failed" warnings (a sustained stream of them means connections are about to be closed as unverified).

## Host responsibilities

- Authentication (JWT validation, session/revocation checks) and the token lifetime policy.
- ALL authorization: group join rules, current group membership, conversation access. The library never trusts client-supplied user/tenant/group ids.
- Persistence: messages, idempotency (`ClientMessageId`), ordering, history endpoints, notification rows, retention.
- Choosing recipients and payload visibility per audience; calling `RevokeAsync` when access is removed.
- Recovery after a reconnect: clients re-fetch history from your API (nothing is replayed).

## Not included

Message storage/outbox, delivery or read receipts, typing/presence, push/email fallbacks, rate limiting, moderation, attachments, multi-instance group eviction (the connection registry is per instance; use `PublishToGroupMembersAsync` and token-bound lifetimes there), Redis availability handling beyond SignalR's own.

## Known limits

- Without an `IRealtimeConnectionValidator`, a session revoked at the host keeps an already open socket until its access token expires (the connection is aborted then and cannot reconnect). Private events are still only sent to recipients authorized at publish time.
- Revalidation closes connections; it does not re-check group membership of the ones it keeps. Private data must go through recipient lists or `PublishToGroupMembersAsync`, which read current access at publish time.
- The Redis backplane and the cross-instance revalidation signal are exercised by integration tests only when `OPSFLOW_TEST_REDIS` points at a Redis; local development runs fully in-process.

## Angular

`frontend/src/app/core/realtime/README.md`: `start`, `on<T>`, `joinGroup` (re-joined after every reconnect), `restart` after token refresh, `stop` on logout.
