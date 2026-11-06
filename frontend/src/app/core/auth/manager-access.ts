import { effect, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TenantService } from './tenant.service';

/**
 * Owner/Manager pages stay mounted while the workspace changes, which the route guard does not see. Leaving to the dashboard as
 * soon as the active workspace no longer grants the role (or the role is unknown) fails closed; the server remains authoritative.
 */
export function redirectUnlessManager(): void {
  const tenant = inject(TenantService);
  const router = inject(Router);
  effect(() => {
    if (tenant.tenantId() && !tenant.isManager()) void router.navigateByUrl('/dashboard');
  });
}
