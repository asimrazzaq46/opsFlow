import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';

export type RealtimeState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
export type AccessTokenFactory = () => string | Promise<string>;
export type HubConnectionFactory = (url: string, accessTokenFactory: AccessTokenFactory) => HubConnection;

/** Overridable (tests, custom transports); the default builds an auto-reconnecting SignalR connection. */
export const REALTIME_HUB_FACTORY = new InjectionToken<HubConnectionFactory>('REALTIME_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => (url, accessTokenFactory) =>
    // UPDATED: Bearer-only API hai (cookies/credentials nahi); default withCredentials=true cross-origin CORS (no credentials) mein negotiate tor deta
    new HubConnectionBuilder().withUrl(url, { accessTokenFactory, withCredentials: false }).withAutomaticReconnect().configureLogging(LogLevel.Warning).build(),
});

const RETRY_AFTER_CLOSE_MS = 10_000;

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
  private args: { url: string; accessTokenFactory: AccessTokenFactory } | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  // UPDATED: wanted groups; the server forgets them on every new connection, so they are re-joined (and re-authorized) after start/reconnect
  private readonly groups = new Set<string>();

  constructor() {
    inject(DestroyRef).onDestroy(() => void this.teardown());
  }

  on<T>(eventName: string): Observable<T> {
    let subject = this.subjects.get(eventName);
    if (!subject) {
      subject = new Subject<unknown>();
      this.subjects.set(eventName, subject);
    }
    return subject.asObservable() as Observable<T>;
  }

  async start(url: string, accessTokenFactory: AccessTokenFactory): Promise<void> {
    await this.teardown();
    this.args = { url, accessTokenFactory };
    const connection = this.factory(url, accessTokenFactory);
    this.connection = connection;
    connection.on('Receive', (name: string, payload: unknown) => this.subjects.get(name)?.next(payload));
    connection.onreconnecting(() => this._state.set('reconnecting'));
    connection.onreconnected(async () => {
      this._state.set('connected');
      await this.rejoinGroups();
      this._reconnected.next();
    });
    connection.onclose(() => {
      if (this.connection !== connection) return;
      this._state.set('disconnected');
      // automatic reconnect gave up (or the token expired): try again later while the session is still wanted
      this.retryTimer = setTimeout(() => void this.restart(), RETRY_AFTER_CLOSE_MS);
    });

    this._state.set('connecting');
    try {
      await connection.start();
      if (this.connection === connection) {
        this._state.set('connected');
        await this.rejoinGroups();
      }
    } catch (error) {
      console.warn('Realtime connection failed', error);
      if (this.connection === connection) {
        this._state.set('disconnected');
        this.retryTimer = setTimeout(() => void this.restart(), RETRY_AFTER_CLOSE_MS);
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
    await this.start(this.args.url, this.args.accessTokenFactory);
    if (this._state() === 'connected') this._reconnected.next();
  }

  async stop(): Promise<void> {
    this.args = null;
    this.groups.clear(); // logout / context change: nothing may be re-joined for the next user
    await this.teardown();
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

  private async teardown(): Promise<void> {
    clearTimeout(this.retryTimer);
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
