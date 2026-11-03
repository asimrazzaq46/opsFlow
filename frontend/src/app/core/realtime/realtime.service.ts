import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';

export type RealtimeState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
export type AccessTokenFactory = () => string | Promise<string>;
export type HubConnectionFactory = (url: string, accessTokenFactory: AccessTokenFactory) => HubConnection;

/**
 * Whether the host still considers the signed-in session valid. `rejected` is definitive (the server said so): reconnecting is pointless.
 * `unknown` means the question could not be answered (network, 429, 5xx, ...): that proves nothing, so reconnecting continues.
 */
export type SessionCheck = 'valid' | 'rejected' | 'unknown';

export interface RealtimeStartOptions {
  /** Asked before a reconnect after the connection was lost, so a revoked session stops retrying instead of hammering the server. */
  checkSession?: () => Promise<SessionCheck>;
}

/** Overridable (tests, custom transports); the default builds an auto-reconnecting SignalR connection. */
export const REALTIME_HUB_FACTORY = new InjectionToken<HubConnectionFactory>('REALTIME_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => (url, accessTokenFactory) =>
    // Bearer-only API (no cookies); the default withCredentials=true would break the cross-origin negotiate request because CORS allows no credentials
    new HubConnectionBuilder().withUrl(url, { accessTokenFactory, withCredentials: false }).withAutomaticReconnect().configureLogging(LogLevel.Warning).build(),
});

const BASE_RETRY_MS = 1_000;
const MAX_RETRY_MS = 60_000;

/** Capped exponential backoff with jitter (half fixed, half random) so many tabs do not reconnect in lockstep. Retrying never stops. */
export function reconnectDelayMs(failures: number, random: () => number = Math.random): number {
  const ceiling = Math.min(MAX_RETRY_MS, BASE_RETRY_MS * 2 ** Math.min(failures, 16));
  return ceiling / 2 + random() * (ceiling / 2);
}

/**
 * Reusable SignalR wrapper (no app imports). The server pushes everything through ONE callback `Receive(name, payload)`;
 * `on(name)` exposes it as a typed stream. One handler is registered per connection and one Subject per event name, so
 * subscribing twice never registers a second hub handler.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly factory = inject(REALTIME_HUB_FACTORY);
  private readonly _state = signal<RealtimeState>('disconnected');
  readonly state = this._state.asReadonly();

  private readonly _reconnected = new Subject<void>();
  /** Emits after an automatic reconnect or a restart: callers resync from the server history (nothing is replayed). */
  readonly reconnected = this._reconnected.asObservable();

  private readonly subjects = new Map<string, Subject<unknown>>();
  private connection: HubConnection | null = null;
  private args: { url: string; accessTokenFactory: AccessTokenFactory; options?: RealtimeStartOptions } | null = null;
  // Wanted groups; the server forgets them on every new connection, so they are re-joined (and re-authorized) after start/reconnect
  private readonly groups = new Set<string>();

  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private onlineHandler: (() => void) | undefined;
  private failures = 0;
  /** Bumped by every start/stop: a late async step (teardown, session check) that sees a newer value must do nothing. */
  private generation = 0;
  private checkingSession = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.generation++;
      void this.teardown();
    });
  }

  on<T>(eventName: string): Observable<T> {
    let subject = this.subjects.get(eventName);
    if (!subject) {
      subject = new Subject<unknown>();
      this.subjects.set(eventName, subject);
    }
    return subject.asObservable() as Observable<T>;
  }

  async start(url: string, accessTokenFactory: AccessTokenFactory, options?: RealtimeStartOptions): Promise<void> {
    const generation = ++this.generation;
    await this.teardown();
    if (generation !== this.generation) return; // a newer start/stop replaced this one while the old connection was closing
    this.args = { url, accessTokenFactory, options };
    const connection = this.factory(url, accessTokenFactory);
    this.connection = connection;
    connection.on('Receive', (name: string, payload: unknown) => this.subjects.get(name)?.next(payload));
    connection.onreconnecting(() => {
      if (this.connection !== connection) return;
      this._state.set('reconnecting');
      void this.verifySession();
    });
    connection.onreconnected(async () => {
      this._state.set('connected');
      this.failures = 0;
      await this.rejoinGroups();
      this._reconnected.next();
    });
    connection.onclose(() => {
      if (this.connection !== connection) return;
      this._state.set('disconnected');
      // automatic reconnect gave up, or the server ended the connection (token expiry, revoked session): decide again, never give up silently
      this.scheduleRetry();
    });

    this._state.set('connecting');
    try {
      await connection.start();
      if (this.connection === connection) {
        this._state.set('connected');
        this.failures = 0;
        await this.rejoinGroups();
      }
    } catch (error) {
      console.warn('Realtime connection failed', error);
      if (this.connection === connection) {
        this._state.set('disconnected');
        this.scheduleRetry();
      }
    }
  }

  /**
   * Joins a host-authorized group. The server decides (false = denied, and the group is forgotten); while disconnected it is joined on connect.
   * Joined groups are re-joined after every reconnect/restart, so a revoked user is refused there. Private data should still be sent to
   * recipient lists by the server, not to groups.
   */
  async joinGroup(group: string): Promise<boolean> {
    this.groups.add(group);
    return this.joinNow(group);
  }

  async leaveGroup(group: string): Promise<void> {
    this.groups.delete(group);
    if (this._state() !== 'connected' || !this.connection) return;
    try {
      await this.connection.invoke('LeaveGroup', group);
    } catch (error) {
      console.warn('Realtime leave failed', error);
    }
  }

  /** Reconnects with whatever the token factory returns now (call after a token refresh). */
  async restart(): Promise<void> {
    if (!this.args) return;
    await this.start(this.args.url, this.args.accessTokenFactory, this.args.options);
    if (this._state() === 'connected') this._reconnected.next();
  }

  async stop(): Promise<void> {
    this.generation++;
    this.args = null;
    this.groups.clear(); // logout / context change: nothing may be re-joined for the next user
    await this.teardown();
  }

  /** One retry at a time. Offline: wait for the browser's `online` event instead of burning timers. */
  private scheduleRetry(): void {
    if (!this.args || this.retryTimer !== undefined || this.onlineHandler) return;
    if (typeof navigator !== 'undefined' && navigator.onLine === false && typeof window !== 'undefined') {
      this.onlineHandler = () => {
        this.clearPending();
        this.failures = 0; // connectivity is back: start over quickly
        void this.retry();
      };
      window.addEventListener('online', this.onlineHandler);
      return;
    }
    this.retryTimer = setTimeout(() => {
      this.retryTimer = undefined;
      void this.retry();
    }, reconnectDelayMs(this.failures++));
  }

  private async retry(): Promise<void> {
    const generation = this.generation;
    const checkSession = this.args?.options?.checkSession;
    if (checkSession) {
      const result = await this.askSession(checkSession);
      if (generation !== this.generation || !this.args) return; // stopped or restarted while asking
      if (result === 'rejected') return void this.stop();
      if (result === 'unknown') return this.scheduleRetry();
    }
    await this.restart();
  }

  /** Once per lost connection (not per attempt): a revoked session ends the reconnect loop right away. */
  private async verifySession(): Promise<void> {
    const generation = this.generation;
    const checkSession = this.args?.options?.checkSession;
    if (!checkSession || this.checkingSession) return;
    this.checkingSession = true;
    try {
      const result = await this.askSession(checkSession);
      if (generation === this.generation && result === 'rejected') await this.stop();
    } finally {
      this.checkingSession = false;
    }
  }

  private async askSession(checkSession: () => Promise<SessionCheck>): Promise<SessionCheck> {
    try {
      return await checkSession();
    } catch {
      return 'unknown'; // a failing check is never a verdict
    }
  }

  private async joinNow(group: string): Promise<boolean> {
    if (this._state() !== 'connected' || !this.connection) return false; // joined later by rejoinGroups()
    try {
      const allowed = await this.connection.invoke<boolean>('JoinGroup', group);
      if (!allowed) this.groups.delete(group);
      return allowed;
    } catch (error) {
      console.warn('Realtime join failed', error);
      return false;
    }
  }

  private async rejoinGroups(): Promise<void> {
    for (const group of [...this.groups]) await this.joinNow(group);
  }

  private clearPending(): void {
    clearTimeout(this.retryTimer);
    this.retryTimer = undefined;
    if (this.onlineHandler) window.removeEventListener('online', this.onlineHandler);
    this.onlineHandler = undefined;
  }

  private async teardown(): Promise<void> {
    this.clearPending();
    const connection = this.connection;
    this.connection = null;
    this._state.set('disconnected');
    try {
      await connection?.stop();
    } catch (error) {
      console.warn('Realtime stop failed', error);
    }
  }
}
