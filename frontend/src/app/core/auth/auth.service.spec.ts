import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { AuthResponse } from '../api/generated/models';
import { RealtimeService } from '../realtime/realtime.service';
import { AuthService, HUB_URL, SESSION_STORAGE_KEY, parseStoredSession } from './auth.service';
import { TenantService } from './tenant.service';

const session = (n: number): AuthResponse => ({
  accessToken: `access-${n}`,
  refreshToken: `refresh-${n}`,
  tokenType: 'Bearer',
  expiresAtUtc: new Date(Date.now() + 15 * 60_000).toISOString(),
  refreshTokenExpiresAtUtc: new Date(Date.now() + 86_400_000).toISOString(),
  user: { id: 'u1', email: 'a@b.c', displayName: 'A' },
});

describe('AuthService', () => {
  const realtime = { start: vi.fn(), restart: vi.fn(), stop: vi.fn(), state: vi.fn(() => 'connected') };
  const tenant = { clear: vi.fn() };
  const invoke = vi.fn();
  let auth: AuthService;

  beforeEach(() => {
    vi.clearAllMocks();
    invoke.mockReset();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: { invoke } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
        { provide: RealtimeService, useValue: realtime },
        { provide: TenantService, useValue: tenant },
      ],
    });
    auth = TestBed.inject(AuthService);
    invoke.mockReturnValueOnce(of(session(1)));
    auth.login('a@b.c', 'pw').subscribe();
  });

  it('persists one versioned session record on login and starts realtime', () => {
    expect(auth.accessToken()).toBe('access-1');
    expect(realtime.start).toHaveBeenCalledOnce();
    expect(parseStoredSession(localStorage.getItem(SESSION_STORAGE_KEY))).toMatchObject({ v: 1, accessToken: 'access-1', refreshToken: 'refresh-1' });
    expect(localStorage.length + sessionStorage.length).toBe(1);
  });

  it('persists the rotated pair after a refresh', () => {
    invoke.mockReturnValueOnce(of(session(2)));
    auth.refresh().subscribe();
    expect(parseStoredSession(localStorage.getItem(SESSION_STORAGE_KEY))?.refreshToken).toBe('refresh-2');
  });

  it('never lets a refresh that completes after logout bring the session back', () => {
    const pending = new Subject<AuthResponse>();
    invoke.mockReturnValueOnce(pending).mockReturnValueOnce(of(null));
    auth.refresh().subscribe({ error: () => undefined });
    auth.logout();
    pending.next(session(9));
    pending.complete();
    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('shares ONE refresh request between concurrent callers and restarts realtime with the new token', () => {
    const pending = new Subject<AuthResponse>();
    invoke.mockReturnValueOnce(pending);
    auth.refresh().subscribe();
    auth.refresh().subscribe();
    expect(invoke).toHaveBeenCalledTimes(2); // login + exactly one refresh
    expect(invoke.mock.calls[1][1]).toEqual({ body: { refreshToken: 'refresh-1' } });

    pending.next(session(2));
    pending.complete();
    expect(auth.accessToken()).toBe('access-2');
    expect(realtime.restart).toHaveBeenCalledOnce();
  });

  it('ends the session (realtime stopped, workspace cleared) when the refresh is rejected with 401', () => {
    const failing = new Subject<AuthResponse>();
    invoke.mockReturnValueOnce(failing);
    auth.refresh().subscribe({ error: () => undefined });
    failing.error(new HttpErrorResponse({ status: 401 }));

    expect(auth.isAuthenticated()).toBe(false);
    expect(realtime.stop).toHaveBeenCalled();
    expect(tenant.clear).toHaveBeenCalled();
  });

  it('adopts a Google sign-in through the same session and sends only credential + challenge', () => {
    invoke.mockReturnValueOnce(of(session(7)));
    auth.loginWithGoogle({ credential: 'id-token', challengeId: 'c1' }).subscribe();

    expect(invoke.mock.calls[1][1]).toEqual({ body: { credential: 'id-token', challengeId: 'c1' } });
    expect(auth.accessToken()).toBe('access-7');
    expect(realtime.start).toHaveBeenCalledTimes(2);
    const stored = localStorage.getItem(SESSION_STORAGE_KEY) ?? '';
    expect(stored).toContain('access-7');
    expect(stored).not.toContain('id-token'); // the Google credential and challenge are never persisted
    expect(stored).not.toContain('c1');
  });

  it('keeps the current session when Google registration still needs email confirmation', () => {
    invoke.mockReturnValueOnce(of(null));
    let result: AuthResponse | null | undefined;
    auth.loginWithGoogle({ credential: 'id-token', challengeId: 'c1' }).subscribe((r) => (result = r));

    expect(result).toBeNull();
    expect(auth.accessToken()).toBe('access-1');
  });

  it('clears local state before the revocation call and still signs out when the server is unreachable', () => {
    const pending = new Subject<void>();
    invoke.mockReturnValueOnce(pending);
    auth.logout();
    expect(auth.accessToken()).toBeNull();
    expect(auth.user()).toBeNull();
    expect(realtime.stop).toHaveBeenCalled();
    expect(tenant.clear).toHaveBeenCalled();
    expect(invoke.mock.calls[1][1]).toEqual({ body: { refreshToken: 'refresh-1' } });
    pending.error(new HttpErrorResponse({ status: 0 }));
    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });

  it('replaces the session with the pair returned by a password change', () => {
    invoke.mockReturnValueOnce(of(session(3)));
    auth.changePassword('old', 'new-password').subscribe();
    expect(invoke.mock.calls[1][1]).toEqual({ body: { currentPassword: 'old', newPassword: 'new-password' } });
    expect(auth.accessToken()).toBe('access-3');
    expect(realtime.restart).toHaveBeenCalled();
  });
});

describe('AuthService realtime session handling', () => {
  const realtime = { start: vi.fn(), restart: vi.fn(), stop: vi.fn(), state: vi.fn(() => 'connected') };
  const invoke = vi.fn();
  const navigate = vi.fn();
  let auth: AuthService;
  const me = { id: 'u1', email: 'a@b.c', displayName: 'A' };
  const nearExpiry = (n: number): AuthResponse => ({ ...session(n), expiresAtUtc: new Date(Date.now() + 30_000).toISOString() });
  const tokenFactory = () => realtime.start.mock.calls.at(-1)![1] as () => Promise<string>;
  const sessionCheck = () => (realtime.start.mock.calls.at(-1)![2] as { checkSession: () => Promise<string> }).checkSession();

  beforeEach(() => {
    vi.clearAllMocks();
    invoke.mockReset();
    navigate.mockReset();
    realtime.state.mockReturnValue('connected');
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: { invoke } },
        { provide: Router, useValue: { navigateByUrl: navigate } },
        { provide: RealtimeService, useValue: realtime },
        { provide: TenantService, useValue: { clear: vi.fn() } },
      ],
    });
    auth = TestBed.inject(AuthService);
    invoke.mockReturnValueOnce(of(session(1)));
    auth.login('a@b.c', 'pw').subscribe();
  });

  it('starts the hub with a token factory and a session check', () => {
    expect(realtime.start).toHaveBeenCalledWith(HUB_URL, expect.any(Function), { checkSession: expect.any(Function) });
  });

  it('hands the hub the current token without rotating anything while the session is healthy', async () => {
    expect(await tokenFactory()()).toBe('access-1');
    expect(await tokenFactory()()).toBe('access-1');
    expect(invoke).toHaveBeenCalledTimes(1); // only the login: no refresh on a plain reconnect
  });

  it('refreshes once, shared between concurrent reconnects, when the token is about to expire, and does not restart the hub that asked', async () => {
    invoke.mockReturnValueOnce(of(nearExpiry(2)));
    auth.login('a@b.c', 'pw').subscribe();
    const pending = new Subject<AuthResponse>();
    invoke.mockReturnValueOnce(pending);
    realtime.state.mockReturnValue('connecting');

    const factory = tokenFactory();
    const first = factory();
    const second = factory();
    pending.next(session(3));
    pending.complete();

    expect(await first).toBe('access-3');
    expect(await second).toBe('access-3');
    expect(invoke).toHaveBeenCalledTimes(3); // two logins + exactly one refresh
    expect(realtime.restart).not.toHaveBeenCalled();
  });

  it('still hands over a token (and lets the connect fail) when the proactive refresh fails', async () => {
    invoke.mockReturnValueOnce(of(nearExpiry(2)));
    auth.login('a@b.c', 'pw').subscribe();
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503 })));

    expect(await tokenFactory()()).toBe('access-2');
    expect(auth.isAuthenticated()).toBe(true); // a 5xx on refresh never signs out
  });

  it('restarts a connected hub after a refresh so it uses the new token', () => {
    invoke.mockReturnValueOnce(of(session(2)));
    auth.refresh().subscribe();
    expect(realtime.restart).toHaveBeenCalledOnce();
  });

  it('reports a valid session when the server answers', async () => {
    invoke.mockReturnValueOnce(of(me));
    expect(await sessionCheck()).toBe('valid');
  });

  it('reports a rejected session once the 401 ended it', async () => {
    invoke.mockReturnValueOnce(
      throwError(() => {
        auth.clear(false); // what the interceptor does when the refresh behind a 401 is rejected
        return new HttpErrorResponse({ status: 401 });
      }),
    );
    expect(await sessionCheck()).toBe('rejected');
    expect(auth.isAuthenticated()).toBe(false);
  });

  it.each([0, 403, 404, 429, 500, 503])('keeps the session and reports unknown for HTTP %i', async (status) => {
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status })));
    expect(await sessionCheck()).toBe('unknown');
    expect(auth.isAuthenticated()).toBe(true);
    expect(navigate).not.toHaveBeenCalled(); // no global logout, no redirect
  });

  it('signs out and redirects to sign-in exactly once when the refresh behind a 401 is rejected', () => {
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 401 })));
    auth.refresh().subscribe({ error: () => undefined });

    expect(auth.isAuthenticated()).toBe(false);
    expect(realtime.stop).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledExactlyOnceWith('/login');
  });

  it('does not sign out when the refresh fails for a transient reason', () => {
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503 })));
    auth.refresh().subscribe({ error: () => undefined });

    expect(auth.isAuthenticated()).toBe(true);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('reports a rejected session when nobody is signed in', async () => {
    const check = sessionCheck;
    auth.clear(false);
    expect(await check()).toBe('rejected');
  });
});

describe('AuthService.restore', () => {
  const realtime = { start: vi.fn(), restart: vi.fn(), stop: vi.fn(), state: vi.fn(() => 'connected') };
  const tenant = { clear: vi.fn() };
  const router = { navigateByUrl: vi.fn() };
  const invoke = vi.fn();
  const store = (over: Partial<AuthResponse> = {}) =>
    localStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify({ v: 1, ...session(1), ...over }));
  let auth: AuthService;

  beforeEach(() => {
    vi.clearAllMocks();
    invoke.mockReset();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: { invoke } },
        { provide: Router, useValue: router },
        { provide: RealtimeService, useValue: realtime },
        { provide: TenantService, useValue: tenant },
      ],
    });
    auth = TestBed.inject(AuthService);
  });

  it('validates a fresh stored session with the server and resumes it', async () => {
    store();
    invoke.mockReturnValueOnce(of({ id: 'u1', email: 'a@b.c', displayName: 'Renamed', hasPassword: true, googleLinked: false }));
    await auth.restore();
    expect(invoke).toHaveBeenCalledOnce();
    expect(auth.accessToken()).toBe('access-1');
    expect(auth.user()?.displayName).toBe('Renamed');
    expect(realtime.start).toHaveBeenCalledOnce();
  });

  it('refreshes a stale access token and persists the rotated pair', async () => {
    store({ expiresAtUtc: new Date(Date.now() - 1000).toISOString() });
    invoke.mockReturnValueOnce(of(session(2)));
    await auth.restore();
    expect(invoke.mock.calls[0][1]).toEqual({ body: { refreshToken: 'refresh-1' } });
    expect(parseStoredSession(localStorage.getItem(SESSION_STORAGE_KEY))?.refreshToken).toBe('refresh-2');
    expect(auth.isAuthenticated()).toBe(true);
  });

  it('clears storage and memory for a revoked session without navigating (the guard redirects)', async () => {
    store({ expiresAtUtc: new Date(Date.now() - 1000).toISOString() });
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 401 })));
    await auth.restore();
    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    expect(realtime.stop).toHaveBeenCalled();
    expect(tenant.clear).toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('keeps the session when the server is unreachable', async () => {
    store();
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })));
    await auth.restore();
    expect(auth.isAuthenticated()).toBe(true);
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).not.toBeNull();
  });

  it.each([
    ['malformed', '{not json'],
    ['wrong version', JSON.stringify({ v: 2, ...session(1) })],
    ['expired refresh token', JSON.stringify({ v: 1, ...session(1), refreshTokenExpiresAtUtc: new Date(Date.now() - 1000).toISOString() })],
  ])('discards a %s record without calling the server', async (_name, raw) => {
    localStorage.setItem(SESSION_STORAGE_KEY, raw);
    await auth.restore();
    expect(invoke).not.toHaveBeenCalled();
    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
  });
});
