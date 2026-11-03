import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { of, throwError } from 'rxjs';
import { UserProfileDto } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { GoogleCredential, GoogleIdentityService } from '../../core/auth/google-identity.service';
import { AccountApi } from './account-api';
import { AccountSecurity } from './account-security';

const me = (over: Partial<UserProfileDto> = {}): UserProfileDto => ({
  id: 'u1',
  email: 'ada@example.com',
  displayName: 'Ada',
  hasPassword: true,
  googleLinked: false,
  ...over,
});

describe('AccountSecurity', () => {
  const account = { profile: vi.fn(), linkGoogle: vi.fn(), sessions: vi.fn(() => of([])), revokeSession: vi.fn(), logoutAll: vi.fn() };
  const auth = { changePassword: vi.fn(), logout: vi.fn(), clear: vi.fn(), updateUser: vi.fn(), user: signal(null) };
  const google = { render: vi.fn(), release: vi.fn() };
  let fixture: ComponentFixture<AccountSecurity>;
  let onCredential: ((c: GoogleCredential) => void) | undefined;
  const el = () => fixture.nativeElement as HTMLElement;

  const create = async (profile: UserProfileDto) => {
    account.profile.mockReturnValue(of(profile));
    fixture = TestBed.createComponent(AccountSecurity);
    await fixture.whenStable();
  };

  const confirmPassword = async (password: string) => {
    fixture.componentInstance['password'].setValue(password);
    fixture.componentInstance['confirm']();
    await fixture.whenStable();
  };

  beforeEach(() => {
    vi.clearAllMocks();
    google.render.mockImplementation(async (_h: HTMLElement, _p: string, _o: unknown, cb: (c: GoogleCredential) => void) => {
      onCredential = cb;
      return { id: 1, expiresAt: Date.now() + 300_000 };
    });
    TestBed.configureTestingModule({
      imports: [AccountSecurity],
      providers: [
        provideTranslateService(),
        { provide: AccountApi, useValue: account },
        { provide: AuthService, useValue: auth },
        { provide: GoogleIdentityService, useValue: google },
      ],
    });
  });

  it('requires the password before any link challenge is requested, then links and shows the linked state', async () => {
    await create(me());
    expect(google.render).not.toHaveBeenCalled();

    await confirmPassword('Secret#1');
    expect(google.render).toHaveBeenCalledTimes(1);
    expect(google.render.mock.calls[0][1]).toBe('link');
    expect(google.render.mock.calls[0][2]).toMatchObject({ password: 'Secret#1' });

    account.linkGoogle.mockReturnValue(of(undefined));
    account.profile.mockReturnValue(of(me({ googleLinked: true })));
    onCredential!({ credential: 'id-token', challengeId: 'c1' });
    await fixture.whenStable();

    expect(account.linkGoogle).toHaveBeenCalledWith({ credential: 'id-token', challengeId: 'c1' });
    expect(el().textContent).toContain('account.linkedNow');
    expect(fixture.componentInstance['password'].value).toBe('');
  });

  it('shows a wrong password and asks for it again instead of retrying', async () => {
    google.render.mockRejectedValueOnce(new HttpErrorResponse({ status: 400, error: { code: 'VALIDATION_ERROR' } }));
    await create(me());
    await confirmPassword('wrong');

    expect(el().textContent).toContain('google.errors.wrongPassword');
    expect(el().querySelector('#link-password')).not.toBeNull();
    expect(google.render).toHaveBeenCalledTimes(1);
  });

  it('reports a Google account that belongs to someone else', async () => {
    await create(me());
    await confirmPassword('Secret#1');
    account.linkGoogle.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'AUTH_GOOGLE_IDENTITY_CONFLICT' } })));
    onCredential!({ credential: 'id-token', challengeId: 'c1' });
    await fixture.whenStable();

    expect(el().textContent).toContain('google.errors.identityConflict');
  });

  it('offers no link for passwordless accounts and shows an existing link', async () => {
    await create(me({ hasPassword: false }));
    expect(el().textContent).toContain('account.passwordRequired');
    expect(el().querySelector('#link-password')).toBeNull();

    await create(me({ googleLinked: true }));
    expect(el().textContent).toContain('account.linked');
  });
});
