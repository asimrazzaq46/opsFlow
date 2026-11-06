import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource, takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, distinctUntilChanged, map, merge } from 'rxjs';
import { TicketState, TicketStatus, TicketPriority } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import {
  PRIORITY_BADGE,
  STATUS_BADGE,
  TICKET_PRIORITIES,
  TICKET_STATUSES,
  httpStatus,
} from './ticket-ui';
import { TicketQuery, TicketSort, TicketsApi } from './tickets-api';

export const PAGE_SIZES = [10, 25, 50] as const;
const SORTS: TicketSort[] = ['updatedAt', 'createdAt', 'number', 'title', 'priority', 'status'];
type ViewState = NonNullable<TicketState>;

/** Reads the list query from the URL; unknown or malformed values fall back to defaults instead of reaching the API. */
export function parseTicketQuery(p: ParamMap): TicketQuery {
  const pick = <T extends string>(key: string, allowed: readonly T[]): T | undefined => {
    const v = p.get(key);
    return allowed.includes(v as T) ? (v as T) : undefined;
  };
  const page = Number(p.get('page'));
  const size = Number(p.get('size'));
  const category = p.get('category');
  return {
    search: p.get('q')?.trim().slice(0, 200) || undefined,
    status: pick('status', TICKET_STATUSES),
    priority: pick('priority', TICKET_PRIORITIES),
    categoryId: category && /^[0-9a-f-]{36}$/i.test(category) ? category : undefined,
    state: pick<ViewState>('state', ['Open', 'Finished', 'Overdue', 'Unstaffed']),
    assignedToMe: p.get('scope') === 'mine' || undefined,
    sortBy: pick('sort', SORTS) ?? 'updatedAt',
    descending: p.get('dir') !== 'asc',
    page: Number.isInteger(page) && page > 0 ? page : 1,
    pageSize: (PAGE_SIZES as readonly number[]).includes(size) ? size : 25,
  };
}

@Component({
  selector: 'app-ticket-list',
  imports: [RouterLink, TranslatePipe, DatePipe, ReactiveFormsModule],
  templateUrl: './ticket-list.html',
  styleUrl: './ticket-list.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketList {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly realtime = inject(RealtimeService);

  protected readonly statuses = TICKET_STATUSES;
  protected readonly priorities = TICKET_PRIORITIES;
  protected readonly pageSizes = PAGE_SIZES;
  protected readonly statusBadge = STATUS_BADGE;
  protected readonly priorityBadge = PRIORITY_BADGE;

  protected readonly query = toSignal(this.route.queryParamMap.pipe(map(parseTicketQuery)), {
    requireSync: true,
  });
  protected searchFocused = false;
  protected readonly search = new FormControl(this.query().search ?? '', { nonNullable: true });

  /** All Tickets: everything the caller may see in this workspace. My Tickets: the caller's own open assignments (resolved by the server). */
  protected readonly scopes = [
    { key: null, label: 'tickets.list.scope.all' },
    { key: 'mine', label: 'tickets.list.scope.mine' },
  ] as const;

  // Staff-only views: customers never receive SLA or staffing data, so those filters are not offered to them.
  protected readonly views = computed<{ key: ViewState | null; label: string }[]>(() => [
    { key: null, label: 'tickets.list.views.all' },
    { key: 'Open', label: 'tickets.list.views.open' },
    ...(this.tenant.isStaff()
      ? [
          { key: 'Overdue' as const, label: 'tickets.list.views.overdue' },
          { key: 'Unstaffed' as const, label: 'tickets.list.views.unstaffed' },
        ]
      : []),
    { key: 'Finished', label: 'tickets.list.views.finished' },
  ]);

  /** Results are tagged with their workspace so a response for a previous workspace is never shown after a switch. */
  protected readonly tickets = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId ? { tenantId, query: this.query() } : undefined;
    },
    stream: ({ params }) =>
      this.api.list(params.query).pipe(map((page) => ({ tenantId: params.tenantId, page }))),
  });
  protected readonly categories = rxResource({
    params: () => this.tenant.tenantId() ?? undefined,
    stream: () => this.api.categories(),
  });

  protected readonly page = computed(() => {
    const r = this.tickets.hasValue() ? this.tickets.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r.page : null;
  });
  protected readonly total = computed(() => Number(this.page()?.totalCount ?? 0));
  protected readonly pages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.query().pageSize)),
  );
  protected readonly range = computed(() => {
    const q = this.query();
    const from = this.total() ? (q.page - 1) * q.pageSize + 1 : 0;
    return { from, to: Math.min(this.total(), q.page * q.pageSize), total: this.total() };
  });
  /** At most five page buttons around the current page. */
  protected readonly pageNumbers = computed(() => {
    const last = this.pages();
    const start = Math.max(1, Math.min(this.query().page - 2, last - 4));
    return Array.from({ length: Math.min(5, last) }, (_, i) => start + i);
  });
  protected readonly filtered = computed(() => {
    const q = this.query();
    return !!(q.search || q.status || q.priority || q.categoryId || q.state || q.assignedToMe);
  });
  protected readonly loadError = computed(() => {
    if (this.tickets.status() !== 'error') return null;
    const status = httpStatus(this.tickets.error());
    return status === 403
      ? 'common.forbidden'
      : status === 400
        ? 'tickets.list.invalidFilter'
        : 'tickets.loadError';
  });

  constructor() {
    // Any staff change in this workspace (and a reconnect) refreshes the current page; the server decides what the caller may see.
    merge(this.realtime.on('ticket.changed'), this.realtime.reconnected)
      .pipe(debounceTime(1000), takeUntilDestroyed())
      .subscribe(() => this.tickets.reload());
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

  protected setFilter(
    key: 'status' | 'priority' | 'category' | 'state' | 'size' | 'scope',
    event: Event | string | null,
  ): void {
    const value =
      typeof event === 'string' || event === null
        ? event
        : (event.target as HTMLSelectElement).value;
    this.navigate({ [key]: value || null, page: null });
  }

  /** Clicking the active column flips the direction; a new column starts descending. */
  protected sort(field: TicketSort): void {
    const q = this.query();
    const descending = q.sortBy === field ? !q.descending : true;
    this.navigate({
      sort: field === 'updatedAt' ? null : field,
      dir: descending ? null : 'asc',
      page: null,
    });
  }

  protected ariaSort(field: TicketSort): 'ascending' | 'descending' | 'none' {
    const q = this.query();
    return q.sortBy !== field ? 'none' : q.descending ? 'descending' : 'ascending';
  }

  protected goTo(page: number): void {
    if (page >= 1 && page <= this.pages()) this.navigate({ page: page === 1 ? null : page });
  }

  protected reset(): void {
    this.search.setValue('', { emitEvent: false });
    this.navigate({
      q: null,
      status: null,
      priority: null,
      category: null,
      state: null,
      scope: null,
      page: null,
    });
  }

  protected count = (v: number | string | null) => Number(v ?? 0);
  protected statusLabel = (s: TicketStatus) => 'tickets.status.' + s;
  protected priorityLabel = (p: TicketPriority) => 'tickets.priority.' + p;

  private navigate(params: Params, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
