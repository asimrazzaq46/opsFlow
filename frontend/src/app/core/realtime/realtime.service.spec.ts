import { TestBed } from '@angular/core/testing';
import { HubConnection } from '@microsoft/signalr';
import { REALTIME_HUB_FACTORY, RealtimeService } from './realtime.service';

class FakeHub {
  receive?: (name: string, payload: unknown) => void;
  reconnecting?: () => void;
  reconnected?: () => void;
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
  onclose() {}
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
