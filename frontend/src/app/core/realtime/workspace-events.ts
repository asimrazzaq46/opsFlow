import { inject } from '@angular/core';
import { Observable, filter } from 'rxjs';
import { TenantService } from '../auth/tenant.service';
import { RealtimeService } from './realtime.service';

/**
 * Events of one name that belong to the ACTIVE workspace. The user's single connection also carries events of their other workspaces;
 * the tenant id in the payload is server-derived, so anything else is dropped. Must be called in an injection context.
 */
export function activeWorkspaceEvents<T extends { tenantId?: string }>(name: string): Observable<T> {
  const tenant = inject(TenantService);
  return inject(RealtimeService)
    .on<T>(name)
    .pipe(filter((e) => !!e && e.tenantId === tenant.tenantId()));
}
