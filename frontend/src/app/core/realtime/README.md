# Realtime (SignalR) wrapper

Framework-level service with no imports from the rest of the app. Pairs with the .NET `OpsFlow.Realtime` library
(hub `/hubs/realtime`, single server callback `Receive(name, payload)`).

## Setup
`npm i @microsoft/signalr`. In a dev server proxy `/hubs` with `"ws": true`. Nothing to register: the service is `providedIn: 'root'`.

## Use
```ts
const realtime = inject(RealtimeService);

// after login (and again after every token refresh: restart() reconnects with the new token)
await realtime.start('/hubs/realtime', () => accessToken());

realtime.on<{ message: Dto }>('ticket.message.created').pipe(takeUntilDestroyed()).subscribe(e => ...);
realtime.reconnected.subscribe(() => resyncFromRestHistory());   // missed events are NOT replayed
realtime.state();                                               // 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

await realtime.stop();                                          // logout / context change
```

- `joinGroup(group)` / `leaveGroup(group)`: the server (host authorizer) decides; groups are re-joined (and re-authorized) after every reconnect/restart and cleared on `stop()`. For private data prefer server-side recipient lists over groups.
- One hub handler per connection, one Subject per event name: repeated `on()` calls never duplicate handlers.
- Automatic reconnect (0/2/10/30 s) plus a 10 s retry when it gives up. The server ends a connection when its access token expires, so the token factory is called again on every reconnect.
- Tests/custom transports: provide `REALTIME_HUB_FACTORY`.
