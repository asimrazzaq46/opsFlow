import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of, throwError } from 'rxjs';
import { AuthResponse } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { GoogleCredential, GoogleIdentityService } from '../../core/auth/google-identity.service';
import { TenantService } from '../../core/auth/tenant.service';
import { Login } from './login';

const session = { accessToken: 'a', refreshToken: 'r' } as AuthResponse;

describe('Login', () => {
  const auth = { login: vi.fn(), loginWithGoogle: vi.fn() };
  const tenant = { load: vi.fn() };
  const router = { navigateByUrl: vi.fn() };
  const google = { render: vi.fn(), release: vi.fn() };
  let fixture: ComponentFixture<Login>;
  let onCredential: ((c: GoogleCredential) => void) | undefined;

  const credential: GoogleCredential = { credential: 'id-token', challengeId: 'c1' };
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(async () => {
    vi.clearAllMocks();
    onCredential = undefined;
    google.render.mockImplementation(async (_host: HTMLElement, _purpose: string, _options: unknown, cb: (c: GoogleCredential) => void) => {
      onCredential = cb;
      return { id: google.render.mock.calls.length, expiresAt: Date.now() + 300_000 };
    });
    TestBed.configureTestingModule({
      imports: [Login],
      providers: [
        provideTranslateService(),
        { provide: AuthService, useValue: auth },
        { provide: TenantService, useValue: tenant },
        { provide: Router, useValue: router },
        { provide: GoogleIdentityService, useValue: google },
      ],
    });
    fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
  });

  it('renders the official Google button next to the unchanged password form', () => {
    expect(google.render).toHaveBeenCalledTimes(1);
    expect(google.render.mock.calls[0][1]).toBe('signIn');
    expect((fixture.nativeElement as HTMLElement).querySelector('#login-password')).not.toBeNull();
  });

  it('signs in with Google through the existing session flow and navigates', async () => {
    auth.loginWithGoogle.mockReturnValue(of(session));
    onCredential!(credential);
    await fixture.whenStable();

    expect(auth.loginWithGoogle).toHaveBeenCalledWith(credential);
    expect(tenant.load).toHaveBeenCalled();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('ignores a second credential while a request runs and fetches a new challenge only afterwards', async () => {
    const pending = new Subject<AuthResponse>();
    auth.loginWithGoogle.mockReturnValue(pending);
    onCredential!(credential);
    onCredential!(credential);
    await fixture.whenStable();
    expect(auth.loginWithGoogle).toHaveBeenCalledTimes(1);
    expect(google.render).toHaveBeenCalledTimes(1); // no new challenge while busy

    pending.error(new HttpErrorResponse({ status: 400, error: { code: 'AUTH_GOOGLE_CHALLENGE_INVALID' } }));
    await fixture.whenStable();
    expect(google.render).toHaveBeenCalledTimes(2); // consumed challenge replaced for the retry
    expect(text()).toContain('google.errors.expired');
  });

  it('explains that an existing password account must link Google instead of signing in', async () => {
    auth.loginWithGoogle.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'AUTH_GOOGLE_LINK_REQUIRED' } })));
    onCredential!(credential);
    await fixture.whenStable();

    expect(text()).toContain('google.errors.linkRequired');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('asks a new account to confirm its email when no session is issued', async () => {
    auth.loginWithGoogle.mockReturnValue(of(null));
    onCredential!(credential);
    await fixture.whenStable();

    expect(text()).toContain('google.verifyEmail');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('keeps password sign-in working', async () => {
    auth.login.mockReturnValue(of(session));
    fixture.componentInstance['form'].setValue({ email: 'a@b.it', password: 'pw' });
    fixture.componentInstance['submit']();
    await fixture.whenStable();
    expect(auth.login).toHaveBeenCalledWith('a@b.it', 'pw');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('hides Google entirely when it is not configured', async () => {
    google.render.mockResolvedValue(null);
    fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    expect(text()).not.toContain('google.or');
    expect(text()).not.toContain('google.signUpHint');
  });
});
