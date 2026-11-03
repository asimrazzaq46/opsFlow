import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { WorkspaceApi } from '../workspace-api';
import { AcceptInvitation } from './accept-invitation';

describe('AcceptInvitation', () => {
  const authenticated = signal(false);
  const auth = { isAuthenticated: authenticated, user: signal({ id: 'u', email: 'ada@example.com', displayName: 'Ada' }), logout: vi.fn() };
  const api = { acceptInvitation: vi.fn() };
  const tenant = { load: vi.fn(() => of([])), select: vi.fn(() => true) };
  let fixture: ComponentFixture<AcceptInvitation>;
  const el = () => fixture.nativeElement as HTMLElement;

  const create = async (token?: string) => {
    fixture = TestBed.createComponent(AcceptInvitation);
    fixture.componentRef.setInput('token', token);
    await fixture.whenStable();
  };

  beforeEach(() => {
    vi.clearAllMocks();
    authenticated.set(false);
    TestBed.configureTestingModule({
      imports: [AcceptInvitation],
      providers: [
        provideTranslateService(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: WorkspaceApi, useValue: api },
        { provide: TenantService, useValue: tenant },
      ],
    });
  });

  it('never accepts for a signed-out visitor and keeps the invitation as the sign-in return target', async () => {
    await create('tok 1');
    expect(api.acceptInvitation).not.toHaveBeenCalled();
    const signIn = el().querySelector<HTMLAnchorElement>('a[href^="/login"]');
    expect(decodeURIComponent(signIn!.getAttribute('href')!)).toContain('returnUrl=/invitations/accept?token=tok%201');
  });

  it('accepts only on an explicit click and shows the server-assigned workspace and role', async () => {
    authenticated.set(true);
    await create('tok');
    expect(api.acceptInvitation).not.toHaveBeenCalled();

    api.acceptInvitation.mockReturnValue(of({ id: 't1', name: 'Acme', slug: 'acme', role: 'Agent' }));
    fixture.componentInstance['accept']();
    await fixture.whenStable();
    expect(api.acceptInvitation).toHaveBeenCalledWith('tok');
    expect(tenant.load).toHaveBeenCalled();
    expect(el().textContent).toContain('invitations.accept.joinedTitle');
    expect(el().textContent).toContain('roles.Agent');

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture.componentInstance['open']();
    expect(tenant.select).toHaveBeenCalledWith('t1');
    expect(navigate).toHaveBeenCalledWith('/dashboard');
  });

  it('explains an email mismatch reported by the server', async () => {
    authenticated.set(true);
    await create('tok');
    api.acceptInvitation.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 403 })));
    fixture.componentInstance['accept']();
    await fixture.whenStable();
    expect(el().textContent).toContain('invitations.accept.mismatchTitle');
  });

  it('treats a link without token as invalid', async () => {
    await create(undefined);
    expect(el().textContent).toContain('invitations.accept.missingText');
  });
});
