import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, finalize, tap } from 'rxjs';
import { Api } from '../api/generated/api';
import { listMyOrganizations } from '../api/generated/functions';
import { OrganizationDto } from '../api/generated/models';

/** Active workspace. Role here only shapes the UI; the backend re-checks every request. */
@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly api = inject(Api);

  readonly organizations = signal<OrganizationDto[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);
  private readonly selectedId = signal<string | null>(null);

  readonly current = computed(() => this.organizations().find((o) => o.id === this.selectedId()) ?? null);
  readonly tenantId = computed(() => this.current()?.id ?? null);
  readonly role = computed(() => this.current()?.role ?? null);
  readonly isStaff = computed(() => this.role() !== null && this.role() !== 'Customer');

  /** Reloads the memberships from the server; a selection that is no longer authorized falls back to the first workspace. */
  load(): Observable<OrganizationDto[]> {
    this.loading.set(true);
    this.error.set(false);
    return this.api.invoke(listMyOrganizations, {}).pipe(
      tap({
        next: (orgs) => {
          this.organizations.set(orgs);
          if (!orgs.some((o) => o.id === this.selectedId())) this.selectedId.set(orgs[0]?.id ?? null);
        },
        error: () => this.error.set(true),
      }),
      finalize(() => this.loading.set(false)),
    );
  }

  /** Only ids returned by the server can become active; tenant-scoped views react to `tenantId` and reload. */
  select(id: string): boolean {
    if (!this.organizations().some((o) => o.id === id)) return false;
    this.selectedId.set(id);
    return true;
  }

  clear(): void {
    this.organizations.set([]);
    this.selectedId.set(null);
    this.error.set(false);
  }
}
