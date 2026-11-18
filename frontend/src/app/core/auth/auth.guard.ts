import { EnvironmentInjector, inject, runInInjectionContext } from '@angular/core';
import { CanActivateFn, GuardResult, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { PendingInvitation } from './pending-invitation';
import { safeReturnUrl } from './return-url';
import { RouteAccess, canAccess } from './route-access';
import { TenantService } from './tenant.service';

/** Signed-out users go to sign-in and come back to the requested page afterwards (validated there by `safeReturnUrl`). */
export const authGuard: CanActivateFn = (_route, state) =>
  inject(AuthService).isAuthenticated() || inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });

/**
 * Sign-in, registration and recovery pages make no sense with an active session. A signed-in visitor continues to the validated
 * `returnUrl` (e.g. an invitation link) or a pending invitation before the workspace picker.
 */
export const guestGuard: CanActivateFn = (route) => {
  if (!inject(AuthService).isAuthenticated()) return true;
  const target = safeReturnUrl(route.queryParamMap.get('returnUrl')) ?? inject(PendingInvitation).url() ?? '/workspaces';
  return inject(Router).parseUrl(target);
};

/**
 * One-time e-mail links (address confirmation, password reset) keep working with a session open: the link names its own account and the
 * server consumes the token. The same pages without a token (the "check your inbox" and request forms) are for signed-out visitors.
 */
export const emailLinkGuard: CanActivateFn = (route, state) =>
  route.queryParamMap.has('token') || guestGuard(route, state);

/**
 * Workspace pages need an active tenant. After a reload the restored session has no memberships in memory yet, so they are
 * loaded here; without any (or when loading fails) the workspace picker shows onboarding or the error with a retry.
 */
export const workspaceGuard: CanActivateFn = async () => {
  const tenant = inject(TenantService);
  const router = inject(Router);
  if (!inject(AuthService).isAuthenticated() || tenant.tenantId()) return true; // authGuard handles signed-out users
  try {
    await firstValueFrom(tenant.load());
  } catch {
    return router.createUrlTree(['/workspaces']);
  }
  return tenant.tenantId() ? true : router.createUrlTree(['/workspaces']);
};

/**
 * Replaces `accessGuard` on a route that can be opened from a notification. The notification names its own workspace in the navigation
 * state (kept by the browser across reload and Back/Forward). It becomes active here, after `canDeactivate` (so a cancelled unsaved-changes
 * prompt never switches it) and after this guard itself initialized the memberships, so it does not depend on any sibling guard. Only a
 * workspace the server lists can be selected; anything else goes to the workspace picker. The ticket is still authorized by the API.
 */
export const notificationWorkspaceGuard: CanActivateFn = async (route, state) => {
  const router = inject(Router);
  const tenant = inject(TenantService);
  const auth = inject(AuthService);
  const injector = inject(EnvironmentInjector);
  const navigation = router.currentNavigation(); // the navigation being guarded; read before the first await
  const target: unknown = navigation?.extras.state?.['notificationTenantId'];
  // accessGuard calls inject(): after an await the guard's injection context is gone, so it must run inside the captured injector.
  const access = () => runInInjectionContext(injector, () => accessGuard(route, state)) as Promise<GuardResult>;
  if (typeof target !== 'string' || !auth.isAuthenticated()) return access(); // authGuard handles signed-out users

  const ready = await (workspaceGuard(route, state) as Promise<GuardResult>);
  if (ready !== true) return ready;
  if (!tenant.organizations().some((o) => o.id === target)) {
    try {
      await firstValueFrom(tenant.load()); // the list may predate a new membership
    } catch {
      return router.createUrlTree(['/workspaces']);
    }
  }

  // A superseded navigation or a sign-out during the awaits above must not change the workspace.
  if (router.currentNavigation()?.id !== navigation?.id || !auth.isAuthenticated()) return false;
  if (target !== tenant.tenantId() && !tenant.select(target)) return router.createUrlTree(['/workspaces']);
  return access();
};

/**
 * Role-gated workspace pages: runs the workspace guard first (guards of one route start together), then checks the route's
 * `data.access` against the active role and sends other roles to the dashboard, which every member may open (no redirect loop).
 * Dependencies are injected before the first await because the injection context ends there.
 */
export const accessGuard: CanActivateFn = async (route, state) => {
  const tenant = inject(TenantService);
  const router = inject(Router);
  const allowed = await (workspaceGuard(route, state) as Promise<GuardResult>); // workspaceGuard is async
  if (allowed !== true) return allowed;
  const access = (route.data['access'] as RouteAccess | undefined) ?? 'member';
  return canAccess(access, tenant.role()) || router.createUrlTree(['/dashboard']);
};
