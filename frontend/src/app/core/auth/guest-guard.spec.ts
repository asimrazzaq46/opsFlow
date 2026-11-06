import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { guestGuard } from './auth.guard';
import { AuthService } from './auth.service';
import { PendingInvitation } from './pending-invitation';

describe('guestGuard', () => {
  const authenticated = signal(false);
  const run = (returnUrl?: string) =>
    TestBed.runInInjectionContext(() =>
      guestGuard({ queryParamMap: { get: () => returnUrl ?? null } } as unknown as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
    );

  beforeEach(() => {
    localStorage.clear();
    authenticated.set(true);
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { isAuthenticated: authenticated } }] });
  });

  const path = (result: unknown) => TestBed.inject(Router).serializeUrl(result as UrlTree);

  it('lets signed-out visitors through', () => {
    authenticated.set(false);
    expect(run()).toBe(true);
  });

  it('sends a signed-in visitor to a safe return URL, then a pending invitation, then the picker', () => {
    expect(path(run('/invitations/accept?token=t'))).toBe('/invitations/accept?token=t');
    expect(path(run('https://evil.test'))).toBe('/workspaces');
    TestBed.inject(PendingInvitation).set('p');
    expect(path(run())).toBe('/invitations/accept?token=p');
  });
});
