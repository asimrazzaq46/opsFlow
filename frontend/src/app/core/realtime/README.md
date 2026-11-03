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
- Automatic reconnect (0/2/10/30 s), then retries with capped exponential backoff (1 s doubling to 60 s, jittered) that never gives up. The server ends a connection when its access token expires or its session is revoked, so the token factory is called again on every reconnect.
- Optional `checkSession` (third argument of `start`): asked once when the connection is lost and before every retry. `'rejected'` (a definitive 401) ends the loop, `'unknown'` (network, 429, 5xx) keeps retrying, `'valid'` reconnects. Offline: no timers, the retry runs when the browser fires `online`. `stop()` cancels the timer, the `online` listener and any pending answer.
- Tests/custom transports: provide `REALTIME_HUB_FACTORY`.
