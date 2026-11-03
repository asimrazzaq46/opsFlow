import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';
import { TenantService } from './tenant.service';

/** Signed-out users go to sign-in and come back to the requested page afterwards (validated there by `safeReturnUrl`). */
export const authGuard: CanActivateFn = (_route, state) =>
  inject(AuthService).isAuthenticated() || inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });

/** Sign-in, registration and recovery pages make no sense with an active session. */
export const guestGuard: CanActivateFn = () => !inject(AuthService).isAuthenticated() || inject(Router).createUrlTree(['/workspaces']);

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
