import { inject } from '@angular/core';
import { CanActivateFn, GuardResult, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { PendingInvitation } from './pending-invitation';
import { safeReturnUrl } from './return-url';
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
 * Owner/Manager-only pages: runs the workspace guard first (guards of one route start together), then redirects other roles.
 * Dependencies are injected before the first await because the injection context ends there.
 */
export const managerGuard: CanActivateFn = async (route, state) => {
  const tenant = inject(TenantService);
  const router = inject(Router);
  const allowed = await (workspaceGuard(route, state) as Promise<GuardResult>); // workspaceGuard is async
  if (allowed !== true) return allowed;
  return tenant.isManager() || router.createUrlTree(['/dashboard']);
};
