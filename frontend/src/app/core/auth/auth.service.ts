import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, of, shareReplay, tap, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { login, logout, refreshSession } from '../api/generated/functions';
import { AuthResponse, UserInfo } from '../api/generated/models';
import { RealtimeService } from '../realtime/realtime.service';
import { TenantService } from './tenant.service';

export const HUB_URL = '/hubs/realtime';
const REFRESH_MARGIN_MS = 60_000;
const MIN_REFRESH_DELAY_MS = 5_000;

/** Tokens live ONLY in these signals (memory): a reload signs the user out by design. */
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

  private inflight: Observable<AuthResponse> | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;

  login(email: string, password: string): Observable<AuthResponse> {
    return this.api.invoke(login, { body: { email, password } }).pipe(
      tap((r) => {
        this.adopt(r);
        void this.realtime.start(HUB_URL, () => this._accessToken() ?? '');
      }),
    );
  }

  /** Single-flight: concurrent callers share one request. 409 (refresh race) is retried once with the newest token; 401 ends the session. */
  refresh(): Observable<AuthResponse> {
    if (this.inflight) return this.inflight;
    const token = this._refreshToken();
    if (!token) return throwError(() => new Error('No session'));

    const call = (t: string) => this.api.invoke(refreshSession, { body: { refreshToken: t } });
    this.inflight = call(token).pipe(
      catchError((err: unknown) =>
        err instanceof HttpErrorResponse && err.status === 409 && this._refreshToken() ? call(this._refreshToken()!) : throwError(() => err),
      ),
      tap((r) => {
        this.adopt(r);
        void this.realtime.restart(); // reconnect with the new token
      }),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse && err.status === 401) this.clear();
        return throwError(() => err);
      }),
      finalize(() => (this.inflight = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inflight;
  }

  logout(): void {
    const token = this._refreshToken();
    const done = () => this.clear();
    if (!token) return done();
    this.api.invoke(logout, { body: { refreshToken: token } }).pipe(catchError(() => of(null))).subscribe(done);
  }

  /** Drops every per-session state (tokens, workspace, realtime) and returns to sign-in. */
  clear(): void {
    clearTimeout(this.timer);
    this._accessToken.set(null);
    this._refreshToken.set(null);
    this.user.set(null);
    this.inflight = null;
    void this.realtime.stop();
    this.tenant.clear();
    void this.router.navigateByUrl('/login');
  }

  private adopt(r: AuthResponse): void {
    this._accessToken.set(r.accessToken);
    this._refreshToken.set(r.refreshToken);
    this.user.set(r.user);
    clearTimeout(this.timer);
    const delay = Math.max(MIN_REFRESH_DELAY_MS, new Date(r.expiresAtUtc).getTime() - Date.now() - REFRESH_MARGIN_MS);
    // shortcut: a failed background refresh (network) is not retried here; the next 401 triggers another refresh
    this.timer = setTimeout(() => this.refresh().pipe(catchError(() => of(null))).subscribe(), delay);
  }
}
