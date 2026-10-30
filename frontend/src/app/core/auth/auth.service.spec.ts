import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject, of } from 'rxjs';
import { Api } from '../api/generated/api';
import { AuthResponse } from '../api/generated/models';
import { RealtimeService } from '../realtime/realtime.service';
import { AuthService } from './auth.service';
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
  const realtime = { start: vi.fn(), restart: vi.fn(), stop: vi.fn() };
  const tenant = { clear: vi.fn() };
  const invoke = vi.fn();
  let auth: AuthService;

  beforeEach(() => {
    vi.clearAllMocks();
    invoke.mockReset();
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

  it('keeps tokens in memory only and starts realtime on login', () => {
    expect(auth.accessToken()).toBe('access-1');
    expect(realtime.start).toHaveBeenCalledOnce();
    expect(localStorage.length + sessionStorage.length).toBe(0);
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
});
