import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, forkJoin, map, of, switchMap } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketActivityDto, TicketActivityType } from '../../core/api/generated/models';
import { mergeBySequence } from './merge';
import { TicketsApi } from './tickets-api';

const ICONS: Partial<Record<TicketActivityType, string>> = {
  Created: 'add_circle',
  DetailsUpdated: 'edit',
  PriorityChanged: 'priority_high',
  CategoryChanged: 'category',
  AgentAssigned: 'person_add',
  AgentUnassigned: 'person_off',
  StatusChanged: 'sync',
  Resolved: 'check_circle',
  Reopened: 'refresh',
  SlaCycleStarted: 'timer',
  SlaCycleCompleted: 'timer_off',
  AttachmentAdded: 'attachment',
};

// Only these entries carry enum names worth showing; other values are ids or internal field lists.
const VALUE_PREFIX: Partial<Record<TicketActivityType, string>> = {
  StatusChanged: 'tickets.status.',
  PriorityChanged: 'tickets.priority.',
};

/** The API's page-size maximum; pages are aligned to it so a page never changes once it is full. */
export const ACTIVITY_PAGE_SIZE = 100;

interface ActivityWindow {
  /** Lowest and highest loaded 1-based page of the oldest-first server order. */
  first: number;
  last: number;
  /** Server total when `last` was read; a smaller total means entries became invisible. */
  total: number;
  /** Id of the first entry of page `last`; it must not move while the timeline only grows. */
  lastHead: string | null;
  /** Loaded entries in persisted sequence order (oldest first), without duplicates. */
  entries: TicketActivityDto[];
}

/** Result of a refresh: either new entries for the existing window, or a window that replaces it. */
type Refreshed =
  | { reset: true; window: ActivityWindow }
  | { reset: false; last: number; total: number; lastHead: string | null; items: TicketActivityDto[] };

const pagesOf = (total: number) => Math.max(1, Math.ceil(total / ACTIVITY_PAGE_SIZE));

/**
 * Read-only audit timeline, newest first. The server pages oldest-first, so the newest page is the LAST one: a probe of page 1 yields
 * the total (and is the data itself for short timelines), then the last page is loaded. "Load earlier" extends the window one page back.
 * A refresh re-reads only the pages from the previously newest one onwards, because an append-only timeline never changes full pages.
 * If the caller's visible set changed instead (e.g. an Agent gaining or losing the assignment shifts every position), it is detected by
 * a smaller total or a moved head of the previously newest page, and the window starts over at the newest page.
 */
@Component({
  selector: 'app-ticket-activity',
  imports: [DatePipe, TranslatePipe],
  templateUrl: './ticket-activity.html',
  styleUrl: './ticket-activity.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketActivity {
  readonly ticketId = input.required<string>();
  readonly version = input<number | string>(0);
  /** Bumped by the parent when audited changes do not change the ticket version (child tasks, attachments, reconnect). */
  readonly refresh = input(0);

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);

  private readonly window = signal<ActivityWindow | null>(null);
  protected readonly entries = computed(() => [...(this.window()?.entries ?? [])].reverse());
  protected readonly hasEarlier = computed(() => (this.window()?.first ?? 1) > 1);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly loadingEarlier = signal(false);
  protected readonly earlierError = signal(false);

  /** Identifies the ticket/workspace the window belongs to; a change starts over instead of merging. */
  private scope: string | null = null;
  /** Bumped by every reload so an older refresh answer can never overwrite newer state. */
  private generation = 0;
  /** Bumped whenever the window is replaced; a pending "load earlier" for the replaced window is then dropped. */
  private resets = 0;
  private request?: Subscription;
  private earlierRequest?: Subscription;

  constructor() {
    effect(() => {
      const scope = `${this.tenant.tenantId()}:${this.ticketId()}`;
      this.version();
      this.refresh();
      untracked(() => {
        if (scope !== this.scope) {
          this.scope = scope;
          this.replace(null);
        }
        this.reload();
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.request?.unsubscribe();
      this.earlierRequest?.unsubscribe();
    });
  }

  /** Loads the newest page, or refreshes the loaded window from its newest page onwards. */
  protected reload(): void {
    const generation = ++this.generation;
    const ticketId = this.ticketId();
    const current = this.window();
    this.request?.unsubscribe();
    if (!current) this.loading.set(true);
    this.error.set(false);
    this.request = this.api
      .activity(ticketId, 1, ACTIVITY_PAGE_SIZE)
      .pipe(
        switchMap((probe): Observable<Refreshed> => {
          const total = Number(probe.totalCount);
          const last = pagesOf(total);
          const fetch = (p: number) => (p === 1 ? of(probe.items) : this.page(ticketId, p));
          const fresh = (items: TicketActivityDto[]): Refreshed => ({
            reset: true,
            window: { first: last, last, total, lastHead: items[0]?.id ?? null, entries: mergeBySequence([], items) },
          });
          if (!current || total < current.total) return fetch(last).pipe(map(fresh));
          // Usually one page; two when the total crossed a page boundary since the last read.
          const pages: Observable<TicketActivityDto[]>[] = [];
          for (let p = current.last; p <= last; p++) pages.push(fetch(p));
          return forkJoin(pages).pipe(
            map((loaded) => {
              const newest = loaded[loaded.length - 1];
              if ((loaded[0][0]?.id ?? null) !== current.lastHead) return fresh(newest);
              return { reset: false, last, total, lastHead: newest[0]?.id ?? null, items: loaded.flat() };
            }),
          );
        }),
      )
      .subscribe({
        next: (r) => {
          if (generation !== this.generation) return;
          const now = this.window();
          if (r.reset || !now) this.replace(r.reset ? r.window : null);
          else
            this.window.set({ ...now, last: r.last, total: r.total, lastHead: r.lastHead, entries: mergeBySequence(now.entries, r.items) });
          this.loading.set(false);
        },
        error: () => {
          if (generation !== this.generation) return;
          this.error.set(true);
          this.loading.set(false);
        },
      });
  }

  protected loadEarlier(): void {
    const current = this.window();
    if (!current || current.first <= 1 || this.loadingEarlier()) return;
    const resets = this.resets;
    const page = current.first - 1;
    this.loadingEarlier.set(true);
    this.earlierError.set(false);
    // Full pages before the newest one never change, so a refresh running meanwhile does not invalidate this page.
    this.earlierRequest = this.page(this.ticketId(), page).subscribe({
      next: (items) => {
        const now = this.window();
        if (resets !== this.resets || !now || now.first !== page + 1) return;
        this.window.set({ ...now, first: page, entries: mergeBySequence(now.entries, items) });
        this.loadingEarlier.set(false);
      },
      error: () => {
        if (resets !== this.resets) return;
        this.earlierError.set(true);
        this.loadingEarlier.set(false);
      },
    });
  }

  /** Replaces the whole window and abandons a pending "load earlier" that belonged to the old one. */
  private replace(window: ActivityWindow | null): void {
    this.resets++;
    this.earlierRequest?.unsubscribe();
    this.loadingEarlier.set(false);
    this.earlierError.set(false);
    this.window.set(window);
  }

  private page(ticketId: string, page: number): Observable<TicketActivityDto[]> {
    return this.api.activity(ticketId, page, ACTIVITY_PAGE_SIZE).pipe(map((p) => p.items));
  }

  protected icon = (a: TicketActivityDto) =>
    ICONS[a.type] ?? (a.type.startsWith('Task') ? 'task_alt' : 'history');

  /**
   * Who acted, exactly as persisted: the actor's name; "Support team" when a Customer is told that staff acted (staff identities stay
   * hidden from Customers); "System" for automation; a neutral label when the name cannot be resolved. Never the signed-in user by default.
   */
  protected actorKey = (a: TicketActivityDto): string | null =>
    a.actorDisplayName
      ? null
      : a.actorUserId
        ? 'tickets.activity.actor.unknown'
        : this.tenant.role() === 'Customer'
          ? 'tickets.activity.actor.support'
          : 'tickets.activity.actor.system';

  /** Sentence key for the event. A self-assignment reads as an ordinary assignment: display names are not identities. */
  protected didKey = (a: TicketActivityDto): string => 'tickets.activity.did.' + a.type;
  protected valueKey = (a: TicketActivityDto, v: string | null) =>
    v && VALUE_PREFIX[a.type] ? VALUE_PREFIX[a.type] + v : null;
}
