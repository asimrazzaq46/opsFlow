import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, distinctUntilChanged, map } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { ConfirmDialog } from '../../shared/components/confirm-dialog/confirm-dialog';
import { redirectUnlessManager } from '../../core/auth/manager-access';
import { isForbidden } from '../tickets/ticket-ui';
import { CustomerListQuery, CustomerRow, CustomerSort, CustomersApi } from './customers-api';

export const CUSTOMER_PAGE_SIZES = [10, 25, 50] as const;
const SORTS: CustomerSort[] = ['name', 'email', 'created'];

/** Reads the list query from the URL; unknown or malformed values fall back to defaults instead of reaching the API. */
export function parseCustomerQuery(p: ParamMap): CustomerListQuery {
  const sort = p.get('sort') as CustomerSort;
  const page = Number(p.get('page'));
  const size = Number(p.get('size'));
  const active = p.get('status');
  return {
    search: p.get('q')?.trim().slice(0, 200) || undefined,
    isActive: active === 'active' ? true : active === 'inactive' ? false : undefined,
    sortBy: SORTS.includes(sort) ? sort : 'name',
    descending: p.get('dir') === 'desc',
    page: Number.isInteger(page) && page > 0 ? page : 1,
    pageSize: (CUSTOMER_PAGE_SIZES as readonly number[]).includes(size) ? size : 25,
  };
}

/** Owner/Manager customer directory. State lives in the URL; the server does all searching, sorting and paging. */
@Component({
  selector: 'app-customers',
  imports: [RouterLink, TranslatePipe, DatePipe, ReactiveFormsModule, ConfirmDialog],
  templateUrl: './customers.html',
  styleUrl: './customers.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Customers {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(CustomersApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly isOwner = this.tenant.isOwner;
  protected readonly pageSizes = CUSTOMER_PAGE_SIZES;
  protected readonly query = toSignal(this.route.queryParamMap.pipe(map(parseCustomerQuery)), {
    requireSync: true,
  });
  protected searchFocused = false;
  protected readonly search = new FormControl(this.query().search ?? '', { nonNullable: true });

  protected readonly confirm = viewChild.required<ConfirmDialog>('confirm');
  protected readonly pending = signal<CustomerRow | null>(null);
  protected readonly busyId = signal<string | null>(null);
  protected readonly message = signal<{ key: string; error: boolean } | null>(null);

  /** Results are tagged with their workspace so a response for a previous workspace is never shown after a switch. */
  protected readonly customers = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId && this.tenant.isManager() ? { tenantId, query: this.query() } : undefined;
    },
    stream: ({ params }) =>
      this.api.list(params.query).pipe(map((page) => ({ tenantId: params.tenantId, page }))),
  });

  protected readonly page = computed(() => {
    const r = this.customers.hasValue() ? this.customers.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r.page : null;
  });
  protected readonly total = computed(() => this.page()?.totalCount ?? 0);
  protected readonly pages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.query().pageSize)),
  );
  protected readonly range = computed(() => {
    const q = this.query();
    const from = this.total() ? (q.page - 1) * q.pageSize + 1 : 0;
    return { from, to: Math.min(this.total(), q.page * q.pageSize), total: this.total() };
  });
  protected readonly pageNumbers = computed(() => {
    const last = this.pages();
    const start = Math.max(1, Math.min(this.query().page - 2, last - 4));
    return Array.from({ length: Math.min(5, last) }, (_, i) => start + i);
  });
  protected readonly filtered = computed(
    () => !!(this.query().search || this.query().isActive !== undefined),
  );
  protected readonly loadError = computed(() => {
    if (this.customers.status() !== 'error') return null;
    return isForbidden(this.customers.error()) ? 'common.forbidden' : 'customers.loadError';
  });

  constructor() {
    redirectUnlessManager();
    this.search.valueChanges
      .pipe(
        debounceTime(350),
        map((v) => v.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((q) => {
        if (q !== (this.query().search ?? '')) this.navigate({ q: q || null, page: null }, true);
      });
    // keeps the box in sync when the URL changes on its own (back/forward), never while the user is typing
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((p) => {
      const q = p.get('q') ?? '';
      if (!this.searchFocused && q.trim() !== this.search.value.trim())
        this.search.setValue(q, { emitEvent: false });
    });
  }

  protected setFilter(key: 'status' | 'size', event: Event): void {
    this.navigate({ [key]: (event.target as HTMLSelectElement).value || null, page: null });
  }

  /** Clicking the active column flips the direction; a new column starts ascending. */
  protected sort(field: CustomerSort): void {
    const q = this.query();
    const descending = q.sortBy === field ? !q.descending : false;
    this.navigate({
      sort: field === 'name' ? null : field,
      dir: descending ? 'desc' : null,
      page: null,
    });
  }

  protected ariaSort(field: CustomerSort): 'ascending' | 'descending' | 'none' {
    const q = this.query();
    return q.sortBy !== field ? 'none' : q.descending ? 'descending' : 'ascending';
  }

  protected goTo(page: number): void {
    if (page >= 1 && page <= this.pages()) this.navigate({ page: page === 1 ? null : page });
  }

  protected reset(): void {
    this.search.setValue('', { emitEvent: false });
    this.navigate({ q: null, status: null, page: null });
  }

  /** Deactivation is confirmed (it blocks new tickets); activation is harmless and applies directly. */
  protected toggle(c: CustomerRow): void {
    if (!this.isOwner()) return;
    if (c.isActive) {
      this.pending.set(c);
      this.confirm().open();
    } else {
      this.applyStatus(c, true);
    }
  }

  protected confirmDeactivate(): void {
    const c = this.pending();
    if (c) this.applyStatus(c, false);
  }

  private applyStatus(c: CustomerRow, isActive: boolean): void {
    const tenantId = this.tenant.tenantId();
    if (this.busyId() || !tenantId) return;
    this.busyId.set(c.id);
    this.message.set(null);
    this.api.setActive(c.id, isActive).subscribe({
      next: () => {
        this.busyId.set(null);
        this.confirm().close();
        if (tenantId !== this.tenant.tenantId()) return;
        this.message.set({
          key: isActive ? 'customers.activated' : 'customers.deactivated',
          error: false,
        });
        this.customers.reload();
      },
      error: (e: unknown) => {
        this.busyId.set(null);
        this.confirm().close();
        this.message.set({
          key: isForbidden(e) ? 'common.forbidden' : 'customers.statusError',
          error: true,
        });
      },
    });
  }

  private navigate(params: Params, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
