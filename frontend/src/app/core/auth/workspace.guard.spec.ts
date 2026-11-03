import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { OrganizationDto } from '../api/generated/models';
import { AuthService } from './auth.service';
import { workspaceGuard } from './auth.guard';
import { TenantService, WORKSPACE_STORAGE_KEY } from './tenant.service';

const org = (id: string): OrganizationDto => ({ id, name: id, role: 'Agent', slug: id });

describe('TenantService selection', () => {
  let invoke: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    localStorage.clear();
    invoke = vi.fn();
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: { invoke } }] });
  });

  it('restores the remembered workspace only when the server still lists it', () => {
    const tenant = TestBed.inject(TenantService);
    localStorage.setItem(WORKSPACE_STORAGE_KEY, 'B');
    invoke.mockReturnValue(of([org('A'), org('B')]));
    tenant.load().subscribe();
    expect(tenant.tenantId()).toBe('B');

    tenant.clear();
    localStorage.setItem(WORKSPACE_STORAGE_KEY, 'foreign');
    invoke.mockReturnValue(of([org('A')]));
    tenant.load().subscribe();
    expect(tenant.tenantId()).toBe('A');
  });

  it('remembers a selection and forgets it on clear', () => {
    const tenant = TestBed.inject(TenantService);
    invoke.mockReturnValue(of([org('A'), org('B')]));
    tenant.load().subscribe();
    expect(tenant.select('nope')).toBe(false);
    expect(tenant.select('B')).toBe(true);
    expect(localStorage.getItem(WORKSPACE_STORAGE_KEY)).toBe('B');
    tenant.clear();
    expect(localStorage.getItem(WORKSPACE_STORAGE_KEY)).toBeNull();
  });
});

describe('workspaceGuard', () => {
  const authenticated = signal(true);
  let load: ReturnType<typeof vi.fn>;
  const tenantId = signal<string | null>(null);

  const run = () =>
    TestBed.runInInjectionContext(() => workspaceGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as Promise<boolean | UrlTree>;

  beforeEach(() => {
    authenticated.set(true);
    tenantId.set(null);
    load = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated: authenticated } },
        { provide: TenantService, useValue: { tenantId, load } },
      ],
    });
  });

  it('loads the memberships after a reload and lets the page open', async () => {
    load.mockImplementation(() => {
      tenantId.set('A');
      return of([org('A')]);
    });
    expect(await run()).toBe(true);
    expect(load).toHaveBeenCalledTimes(1);
  });

  it('sends users without a workspace, or after a failed load, to the workspace picker', async () => {
    const picker = TestBed.inject(Router).createUrlTree(['/workspaces']);
    load.mockReturnValue(of([]));
    expect(String(await run())).toBe(String(picker));
    load.mockReturnValue(throwError(() => new Error('offline')));
    expect(String(await run())).toBe(String(picker));
  });

  it('does nothing when a workspace is active or the user is signed out', async () => {
    tenantId.set('A');
    expect(await run()).toBe(true);
    tenantId.set(null);
    authenticated.set(false);
    expect(await run()).toBe(true);
    expect(load).not.toHaveBeenCalled();
  });
});
