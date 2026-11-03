import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { safeReturnUrl } from './return-url';
import { TenantService } from './tenant.service';

/**
 * Where a fresh session lands: a validated return URL (e.g. a pending invitation) first, otherwise workspace onboarding for
 * users without memberships, the dashboard for exactly one workspace and the workspace picker for several.
 */
@Injectable({ providedIn: 'root' })
export class PostSignIn {
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  enter(returnUrl: string | null): void {
    const target = safeReturnUrl(returnUrl);
    this.tenant.load().subscribe({
      next: (orgs) => void this.router.navigateByUrl(target ?? (orgs.length === 0 ? '/workspaces/new' : orgs.length === 1 ? '/dashboard' : '/workspaces')),
      // The picker shows the load error with a retry.
      error: () => void this.router.navigateByUrl(target ?? '/workspaces'),
    });
  }
}
