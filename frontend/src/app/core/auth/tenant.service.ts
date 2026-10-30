import { Injectable, computed, inject, signal } from '@angular/core';
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

  load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.invoke(listMyOrganizations, {}).subscribe({
      next: (orgs) => {
        this.organizations.set(orgs);
        if (!orgs.some((o) => o.id === this.selectedId())) this.selectedId.set(orgs[0]?.id ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  select(id: string): void {
    this.selectedId.set(id);
  }

  clear(): void {
    this.organizations.set([]);
    this.selectedId.set(null);
  }
}
