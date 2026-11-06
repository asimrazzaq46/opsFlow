import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, firstValueFrom, of, shareReplay, tap, throwError, timeout } from 'rxjs';
import { Api } from '../api/generated/api';
import { changePassword, getCurrentUser, login, logout, refreshSession, signInWithGoogle } from '../api/generated/functions';
import { AuthResponse, UserInfo } from '../api/generated/models';
import { RealtimeService, SessionCheck } from '../realtime/realtime.service';
import { GoogleCredential } from './google-identity.service';
import { TenantService } from './tenant.service';

export const HUB_URL = '/hubs/realtime';
export const SESSION_STORAGE_KEY = 'opsflow.session.v1';
const REFRESH_MARGIN_MS = 60_000;
const MIN_REFRESH_DELAY_MS = 5_000;
const RESTORE_TIMEOUT_MS = 10_000;

/** The single persisted representation of a session (versioned key and payload). Never holds passwords or Google data. */
export interface StoredSession {
  v: 1;
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
  refreshTokenExpiresAtUtc: string;
  user: UserInfo;
}

const isText = (v: unknown): v is string => typeof v === 'string' && v.length > 0;
const isDate = (v: unknown): boolean => isText(v) && !Number.isNaN(Date.parse(v));

/** Returns a structurally valid stored session or null; anything malformed is treated as absent. */
export function parseStoredSession(raw: string | null): StoredSession | null {
  if (!raw) return null;
  try {
    const s = JSON.parse(raw) as Partial<StoredSession> | null;
    const u = s?.user as Partial<UserInfo> | undefined;
    const ok =
      s?.v === 1 && isText(s.accessToken) && isText(s.refreshToken) && isDate(s.expiresAtUtc) && isDate(s.refreshTokenExpiresAtUtc) &&
      isText(u?.id) && isText(u?.email) && typeof u?.displayName === 'string';
    return ok ? (s as StoredSession) : null;
  } catch {
    return null;
  }
}

const toStored = (r: AuthResponse): StoredSession => ({
  v: 1,
  accessToken: r.accessToken,
  refreshToken: r.refreshToken,
  expiresAtUtc: r.expiresAtUtc,
  refreshTokenExpiresAtUtc: r.refreshTokenExpiresAtUtc,
  user: r.user,
});

/**
 * Owns the session. Tokens live in signals and are mirrored to localStorage so a reload or browser restart keeps the user signed in.
 * Trade-off (explicit decision): localStorage is readable by any script on this origin, so XSS protection (CSP, no third-party scripts
 * besides Google Identity Services, Angular sanitization) remains the real defence. Tokens are only sent by the interceptor to
 * same-origin `/api` routes and to the realtime hub.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(Api);
  private readonly realtime = inject(RealtimeService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  private readonly _accessToken = signal<string | null>(null);
  private readonly _refreshToken = signal<string | null>(null);
  readonly accessToken = this._accessToken.asReadonly();
  readonly user = signal<UserInfo | null>(null);
  readonly isAuthenticated = computed(() => this._accessToken() !== null);

  private session: StoredSession | null = null;
  private inflight: Observable<AuthResponse> | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** Bumped on every sign-out; a response started under an older epoch must not bring the session back. */
  private epoch = 0;
  /** During startup the guards perform the redirect (keeping `returnUrl`), so a rejected session is cleared without navigating. */
  private restoring = false;

  /**
   * Runs once before the first navigation, so guards and protected UI only see a resolved state. The stored session is validated by
   * the server: `/auth/me` (the interceptor refreshes on 401) or, for a stale access token, a refresh. A 400/401 ends it; network
   * errors and timeouts prove nothing about revocation, so the session is kept and every later request is re-checked by the server.
   */
  async restore(): Promise<void> {
    const stored = parseStoredSession(this.read());
    if (!stored || Date.parse(stored.refreshTokenExpiresAtUtc) <= Date.now()) return this.forget();

    this.apply(stored);
    const epoch = this.epoch;
    this.restoring = true;
    try {
      if (Date.parse(stored.expiresAtUtc) - Date.now() <= REFRESH_MARGIN_MS) {
        await firstValueFrom(this.refresh().pipe(timeout(RESTORE_TIMEOUT_MS)));
      } else {
        const me = await firstValueFrom(this.api.invoke(getCurrentUser, {}).pipe(timeout(RESTORE_TIMEOUT_MS)));
        if (this.session) this.session = { ...this.session, user: { id: me.id, email: me.email, displayName: me.displayName } };
      }
    } catch (err) {
      if (err instanceof HttpErrorResponse && (err.status === 400 || err.status === 401) && this.epoch === epoch) this.clear(false);
    } finally {
      this.restoring = false;
    }
    if (this.epoch !== epoch || !this.session) return;
    this.adopt(this.session);
    this.connectRealtime();
  }

  login(email: string, password: string): Observable<AuthResponse> {
    return this.api.invoke(login, { body: { email, password } }).pipe(tap((r) => this.begin(r)));
  }

  /**
   * Exchanges a Google credential for the regular OpsFlow session (same tokens and lifecycle as password login).
   * Emits null when the new account must confirm its email first (202, no session).
   */
  loginWithGoogle(credential: GoogleCredential): Observable<AuthResponse | null> {
    return this.api.invoke(signInWithGoogle, { body: credential }).pipe(
      tap((r) => {
        if (r) this.begin(r);
      }),
    );
  }

  /**
   * Single-flight: concurrent callers share one request. 409 (refresh race) is retried once with the newest token; 401 ends the session.
   * Another tab of the same user may already have rotated the token, so the newest stored one is used to avoid a replay.
   */
  refresh(): Observable<AuthResponse> {
    if (this.inflight) return this.inflight;
    if (!this.newestRefreshToken()) return throwError(() => new Error('No session'));

    const epoch = this.epoch;
    const call = () => this.api.invoke(refreshSession, { body: { refreshToken: this.newestRefreshToken()! } });
    this.inflight = call().pipe(
      catchError((err: unknown) =>
        err instanceof HttpErrorResponse && err.status === 409 && this.newestRefreshToken() ? call() : throwError(() => err),
      ),
      tap((r) => {
        if (this.epoch !== epoch) return; // signed out meanwhile: the rotated pair is dropped
        this.adopt(toStored(r));
        // A connected hub is moved to the new token. Otherwise the pending (re)connect asks for its token afterwards, and restarting here
        // would tear down the very attempt that triggered this refresh.
        if (this.realtime.state() === 'connected') void this.realtime.restart();
      }),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse && err.status === 401 && this.epoch === epoch) this.clear(!this.restoring);
        return throwError(() => err);
      }),
      finalize(() => (this.inflight = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inflight;
  }

  /**
   * The refresh token is captured, then local state and storage are dropped so nothing protected stays usable while the revocation
   * runs. Revocation is best effort (an unreachable server leaves the refresh token to expire on its own).
   */
  logout(redirect = true): void {
    const token = this._refreshToken();
    this.clear(redirect);
    if (token) this.api.invoke(logout, { body: { refreshToken: token } }).pipe(catchError(() => of(null))).subscribe();
  }

  /** The server revokes every session and returns a new pair for this device, which replaces the current one. */
  changePassword(currentPassword: string, newPassword: string): Observable<AuthResponse> {
    return this.api.invoke(changePassword, { body: { currentPassword, newPassword } }).pipe(
      tap((r) => {
        this.adopt(toStored(r));
        void this.realtime.restart();
      }),
    );
  }

  /** Drops every per-session state (tokens, storage, workspace, realtime) and by default returns to sign-in. */
  clear(redirect = true): void {
    this.epoch++;
    clearTimeout(this.timer);
    this.forget();
    this.session = null;
    this._accessToken.set(null);
    this._refreshToken.set(null);
    this.user.set(null);
    this.inflight = null;
    void this.realtime.stop();
    this.tenant.clear();
    if (redirect) void this.router.navigateByUrl('/login');
  }

  /** Keeps the persisted copy in sync after a local profile change (the access token carries no display name). */
  updateUser(patch: Partial<Pick<UserInfo, 'displayName'>>): void {
    if (!this.session) return;
    this.session = { ...this.session, user: { ...this.session.user, ...patch } };
    this.user.set(this.session.user);
    this.write();
  }

  private begin(r: AuthResponse): void {
    this.adopt(toStored(r));
    this.connectRealtime();
  }

  private connectRealtime(): void {
    void this.realtime.start(HUB_URL, () => this.freshAccessToken(), { checkSession: () => this.checkSession() });
  }

  /**
   * Token for every (re)connect of the hub. It is rotated through the single-flight refresh ONLY when it is about to expire, so a
   * plain disconnect of a healthy session never rotates the refresh token. A failed refresh is the retry policy's business.
   */
  private async freshAccessToken(): Promise<string> {
    const expiresAt = this.session ? Date.parse(this.session.expiresAtUtc) : Number.POSITIVE_INFINITY;
    if (expiresAt - Date.now() <= REFRESH_MARGIN_MS) {
      try {
        await firstValueFrom(this.refresh().pipe(timeout(RESTORE_TIMEOUT_MS)));
      } catch {
        /* the connect attempt fails and is retried (or the session ended, which stops it) */
      }
    }
    return this._accessToken() ?? '';
  }

  /**
   * Asks the server whether this session is still valid via `/auth/me` (the interceptor refreshes once on 401, and a 401 on that refresh
   * signs out). Only a signed-out result is `rejected`; 403, 404, 429, 5xx, timeouts and network errors prove nothing about revocation.
   */
  async checkSession(): Promise<SessionCheck> {
    if (!this.session) return 'rejected';
    try {
      await firstValueFrom(this.api.invoke(getCurrentUser, {}).pipe(timeout(RESTORE_TIMEOUT_MS)));
      return 'valid';
    } catch {
      return this.session ? 'unknown' : 'rejected';
    }
  }

  private apply(s: StoredSession): void {
    this.session = s;
    this._accessToken.set(s.accessToken);
    this._refreshToken.set(s.refreshToken);
    this.user.set(s.user);
  }

  private adopt(s: StoredSession): void {
    this.apply(s);
    this.write();
    clearTimeout(this.timer);
    const delay = Math.max(MIN_REFRESH_DELAY_MS, Date.parse(s.expiresAtUtc) - Date.now() - REFRESH_MARGIN_MS);
    // shortcut: a failed background refresh (network) is not retried here; the next 401 triggers another refresh
    this.timer = setTimeout(() => this.refresh().pipe(catchError(() => of(null))).subscribe(), delay);
  }

  /** The same user's newer token in storage (rotated by another tab) wins over the one in memory; another user's never does. */
  private newestRefreshToken(): string | null {
    const stored = parseStoredSession(this.read());
    return stored && stored.user.id === this.user()?.id ? stored.refreshToken : this._refreshToken();
  }

  private write(): void {
    try {
      if (this.session) localStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(this.session));
    } catch {
      /* storage unavailable or full: the session still works for this page */
    }
  }

  private read(): string | null {
    try {
      return localStorage.getItem(SESSION_STORAGE_KEY);
    } catch {
      return null;
    }
  }

  private forget(): void {
    try {
      localStorage.removeItem(SESSION_STORAGE_KEY);
    } catch {
      /* storage unavailable */
    }
  }
}
