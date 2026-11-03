import { TestBed } from '@angular/core/testing';
import { HubConnection } from '@microsoft/signalr';
import { REALTIME_HUB_FACTORY, RealtimeService, SessionCheck, reconnectDelayMs } from './realtime.service';

class FakeHub {
  receive?: (name: string, payload: unknown) => void;
  reconnecting?: () => void;
  reconnected?: () => void;
  closed?: () => void;
  registrations = 0;
  invocations: string[] = [];
  allow = true;
  stopped = false;
  static failStart = false;
  on(_: string, h: (name: string, payload: unknown) => void) {
    this.registrations++;
    this.receive = h;
  }
  onreconnecting(h: () => void) {
    this.reconnecting = h;
  }
  onreconnected(h: () => void) {
    this.reconnected = h;
  }
  onclose(h: () => void) {
    this.closed = h;
  }
  invoke(method: string, group: string) {
    this.invocations.push(`${method}:${group}`);
    return Promise.resolve(method === 'JoinGroup' ? this.allow : undefined);
  }
  start() {
    return FakeHub.failStart ? Promise.reject(new Error('down')) : Promise.resolve();
  }
  stop() {
    this.stopped = true;
    return Promise.resolve();
  }
}

describe('RealtimeService', () => {
  let hubs: FakeHub[];
  let service: RealtimeService;

  beforeEach(() => {
    hubs = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: REALTIME_HUB_FACTORY,
          useValue: () => {
            const h = new FakeHub();
            hubs.push(h);
            return h as unknown as HubConnection;
          },
        },
      ],
    });
    service = TestBed.inject(RealtimeService);
  });

  it('tracks connection state through start, reconnect and stop', async () => {
    expect(service.state()).toBe('disconnected');
    await service.start('/hub', () => 't');
    expect(service.state()).toBe('connected');
    hubs[0].reconnecting!();
    expect(service.state()).toBe('reconnecting');
    hubs[0].reconnected!();
    expect(service.state()).toBe('connected');
    await service.stop();
    expect(service.state()).toBe('disconnected');
    expect(hubs[0].stopped).toBe(true);
  });

  it('registers one hub handler and fans events out per name without duplicates', async () => {
    const seen: unknown[] = [];
    service.on('a').subscribe((p) => seen.push(p));
    service.on('a').subscribe((p) => seen.push(p)); // both share the same stream
    await service.start('/hub', () => 't');
    hubs[0].receive!('a', 1);
    hubs[0].receive!('b', 2); // nobody listens: ignored
    expect(hubs[0].registrations).toBe(1);
    expect(seen).toEqual([1, 1]);
  });

  it('restart replaces the connection and signals a resync', async () => {
    let resyncs = 0;
    service.reconnected.subscribe(() => resyncs++);
    await service.start('/hub', () => 't');
    await service.restart();
    expect(hubs.length).toBe(2);
    expect(hubs[0].stopped).toBe(true);
    expect(resyncs).toBe(1);
    await service.stop();
  });

  it('re-joins wanted groups after a reconnect and forgets groups the server denies', async () => {
    await service.start('/hub', () => 't');
    expect(await service.joinGroup('room-1')).toBe(true);
    hubs[0].allow = false;
    expect(await service.joinGroup('room-2')).toBe(false); // denied: not remembered
    hubs[0].allow = true;
    hubs[0].invocations.length = 0;

    await hubs[0].reconnected!();
    expect(hubs[0].invocations).toEqual(['JoinGroup:room-1']);

    await service.restart(); // a new connection re-joins too
    expect(hubs[1].invocations).toEqual(['JoinGroup:room-1']);
    await service.stop();
  });

  it('joins queued groups on connect, never twice, and forgets them on stop or leave', async () => {
    expect(await service.joinGroup('room-1')).toBe(false); // not connected yet
    expect(await service.joinGroup('room-1')).toBe(false);
    await service.start('/hub', () => 't');
    expect(hubs[0].invocations).toEqual(['JoinGroup:room-1']);

    await service.leaveGroup('room-1');
    await service.restart();
    expect(hubs[1].invocations).toEqual([]);

    await service.joinGroup('room-2');
    await service.stop(); // logout: nothing survives for the next user
    await service.start('/hub', () => 't');
    expect(hubs[2].invocations).toEqual([]);
    await service.stop();
  });

  it('warns and stays disconnected when the connection cannot be started', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    FakeHub.failStart = true;
    await service.start('/hub', () => 't');
    FakeHub.failStart = false;
    expect(warn).toHaveBeenCalled();
    expect(service.state()).toBe('disconnected');
    await service.stop(); // also cancels the scheduled retry
    warn.mockRestore();
  });
});

describe('RealtimeService reconnect policy', () => {
  let hubs: FakeHub[];
  let service: RealtimeService;
  let checkSession: ReturnType<typeof vi.fn<() => Promise<SessionCheck>>>;
  const settle = () => vi.advanceTimersByTimeAsync(0);
  const setOnline = (value: boolean) => Object.defineProperty(navigator, 'onLine', { value, configurable: true });

  beforeEach(() => {
    vi.useFakeTimers();
    vi.spyOn(Math, 'random').mockReturnValue(0); // delay = half of the backoff ceiling
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    hubs = [];
    FakeHub.failStart = false;
    TestBed.configureTestingModule({
      providers: [
        {
          provide: REALTIME_HUB_FACTORY,
          useValue: () => {
            const h = new FakeHub();
            hubs.push(h);
            return h as unknown as HubConnection;
          },
        },
      ],
    });
    service = TestBed.inject(RealtimeService);
    checkSession = vi.fn<() => Promise<SessionCheck>>().mockResolvedValue('valid');
  });

  afterEach(async () => {
    await service.stop();
    Reflect.deleteProperty(navigator, 'onLine');
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('grows the delay exponentially, caps it at one minute and never gives up', () => {
    const lowest = (n: number) => reconnectDelayMs(n, () => 0);
    expect(lowest(0)).toBe(500);
    expect(lowest(1)).toBe(1_000);
    expect(lowest(2)).toBe(2_000);
    for (let n = 0; n < 200; n++) {
      expect(reconnectDelayMs(n, () => 1)).toBeLessThanOrEqual(60_000);
      expect(reconnectDelayMs(n, () => 0)).toBeGreaterThanOrEqual(500);
    }
    expect(reconnectDelayMs(200, () => 1)).toBe(60_000);
  });

  it('retries a lost connection only after the session was confirmed, then resyncs', async () => {
    let resyncs = 0;
    service.reconnected.subscribe(() => resyncs++);
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].closed!();
    expect(service.state()).toBe('disconnected');
    expect(checkSession).not.toHaveBeenCalled(); // not before the backoff delay
    await vi.advanceTimersByTimeAsync(500);
    await settle();

    expect(checkSession).toHaveBeenCalledOnce();
    expect(hubs.length).toBe(2);
    expect(service.state()).toBe('connected');
    expect(resyncs).toBe(1);
  });

  it('stops retrying for good once the server rejected the session', async () => {
    checkSession.mockResolvedValue('rejected');
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].closed!();
    await vi.advanceTimersByTimeAsync(120_000);
    await settle();

    expect(hubs.length).toBe(1);
    expect(service.state()).toBe('disconnected');
    expect(vi.getTimerCount()).toBe(0);
    await service.restart(); // nothing is wanted any more
    expect(hubs.length).toBe(1);
  });

  it('keeps retrying with capped backoff while the answer is unknown (network, 429, 5xx) and reconnects when it recovers', async () => {
    checkSession.mockResolvedValue('unknown');
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].closed!();
    for (let i = 0; i < 20; i++) await vi.advanceTimersByTimeAsync(60_000); // twenty minutes without an answer
    expect(checkSession.mock.calls.length).toBeGreaterThanOrEqual(20);
    expect(hubs.length).toBe(1); // never connected, never gave up, never logged out

    checkSession.mockResolvedValue('valid');
    await vi.advanceTimersByTimeAsync(60_000);
    await settle();
    expect(hubs.length).toBe(2);
    expect(service.state()).toBe('connected');
  });

  it('treats a check that throws as "unknown", not as a verdict', async () => {
    checkSession.mockRejectedValueOnce(new Error('boom')).mockResolvedValue('valid');
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].closed!();
    await vi.advanceTimersByTimeAsync(500);
    expect(hubs.length).toBe(1);
    await vi.advanceTimersByTimeAsync(2_000);
    await settle();

    expect(checkSession).toHaveBeenCalledTimes(2);
    expect(hubs.length).toBe(2);
  });

  it('backs off between failed starts and connects as soon as the server is back', async () => {
    FakeHub.failStart = true;
    await service.start('/hub', () => 't');
    expect(service.state()).toBe('disconnected');

    await vi.advanceTimersByTimeAsync(600_000);
    expect(hubs.length).toBeGreaterThan(5); // kept trying, with growing gaps (a fixed 10 s would give ~60)
    expect(hubs.length).toBeLessThan(40);

    FakeHub.failStart = false;
    await vi.advanceTimersByTimeAsync(60_000);
    await settle();
    expect(service.state()).toBe('connected');
  });

  it('never leaves two live connections behind when restarts overlap', async () => {
    await service.start('/hub', () => 't');

    await Promise.all([service.restart(), service.restart(), service.restart()]);

    expect(hubs.filter((h) => !h.stopped).length).toBe(1);
    expect(service.state()).toBe('connected');
  });

  it('waits for the browser to come back online instead of polling, then retries at once', async () => {
    await service.start('/hub', () => 't', { checkSession });
    setOnline(false);

    hubs[0].closed!();
    await vi.advanceTimersByTimeAsync(3_600_000);
    expect(checkSession).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);

    setOnline(true);
    window.dispatchEvent(new Event('online'));
    await settle();
    await settle();

    expect(checkSession).toHaveBeenCalledOnce();
    expect(hubs.length).toBe(2);
    expect(service.state()).toBe('connected');
  });

  it('cancels the pending retry and the online listener on stop', async () => {
    await service.start('/hub', () => 't', { checkSession });
    hubs[0].closed!();
    await service.stop();
    await vi.advanceTimersByTimeAsync(600_000);
    expect(checkSession).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);

    await service.start('/hub', () => 't', { checkSession });
    setOnline(false);
    hubs[1].closed!();
    await service.stop();
    setOnline(true);
    window.dispatchEvent(new Event('online'));
    await settle();
    expect(checkSession).not.toHaveBeenCalled();
    expect(hubs.length).toBe(2);
  });

  it('ignores a session answer that arrives after stop', async () => {
    let answer!: (c: SessionCheck) => void;
    checkSession.mockReturnValue(new Promise<SessionCheck>((resolve) => (answer = resolve)));
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].closed!();
    await vi.advanceTimersByTimeAsync(500);
    expect(checkSession).toHaveBeenCalledOnce();
    await service.stop();
    answer('valid');
    await settle();

    expect(hubs.length).toBe(1);
  });

  it('asks once, while reconnecting, whether the session is still valid and ends the loop when it was rejected', async () => {
    let answer!: (c: SessionCheck) => void;
    checkSession.mockReturnValue(new Promise<SessionCheck>((resolve) => (answer = resolve)));
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].reconnecting!();
    hubs[0].reconnecting!();
    expect(checkSession).toHaveBeenCalledOnce();
    answer('rejected');
    await settle();

    expect(hubs[0].stopped).toBe(true);
    expect(service.state()).toBe('disconnected');
  });

  it('leaves the automatic reconnect alone when the session is still valid', async () => {
    await service.start('/hub', () => 't', { checkSession });

    hubs[0].reconnecting!();
    await settle();

    expect(hubs[0].stopped).toBe(false);
    expect(service.state()).toBe('reconnecting');
  });
});
