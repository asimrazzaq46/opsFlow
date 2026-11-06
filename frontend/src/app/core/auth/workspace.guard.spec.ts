import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { OrganizationDto } from '../api/generated/models';
import { AuthService } from './auth.service';
import { accessGuard, workspaceGuard } from './auth.guard';
import { RouteAccess, canAccess } from './route-access';
import { TenantService, WORKSPACE_STORAGE_KEY } from './tenant.service';

const org = (id: string): OrganizationDto => ({ id, name: id, role: 'Agent', slug: id, membershipId: 'm-' + id });

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

describe('route access', () => {
  it('matches the approved role matrix', () => {
    const roles = ['TenantOwner', 'Manager', 'Agent', 'Customer'] as const;
    const allowed = (access: RouteAccess) => roles.filter((r) => canAccess(access, r));
    expect(allowed('member')).toEqual(['TenantOwner', 'Manager', 'Agent', 'Customer']);
    expect(allowed('staff')).toEqual(['TenantOwner', 'Manager', 'Agent']);
    expect(allowed('manager')).toEqual(['TenantOwner', 'Manager']);
    expect(canAccess('member', null)).toBe(false);
  });

  describe('accessGuard', () => {
    const role = signal<string | null>('Agent');

    const run = (access: RouteAccess) =>
      TestBed.runInInjectionContext(() =>
        accessGuard({ data: { access } } as unknown as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
      ) as Promise<boolean | UrlTree>;

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          { provide: AuthService, useValue: { isAuthenticated: signal(true) } },
          { provide: TenantService, useValue: { tenantId: signal('A'), role, load: vi.fn() } },
        ],
      });
    });

    it('opens eligible pages and sends other roles to the dashboard', async () => {
      const dashboard = String(TestBed.inject(Router).createUrlTree(['/dashboard']));
      role.set('Agent');
      expect(await run('staff')).toBe(true);
      expect(String(await run('manager'))).toBe(dashboard);
      role.set('Customer');
      expect(String(await run('staff'))).toBe(dashboard);
      expect(await run('member')).toBe(true);
      role.set('Manager');
      expect(await run('manager')).toBe(true);
    });
  });
});
