import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Params, ParamMap, Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { map } from 'rxjs';
import { NotificationDto, NotificationType } from '../../core/api/generated/models';
import { NotificationsService } from '../../core/api/generated/services';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { NotificationService, hasTicket } from '../../core/notifications/notification.service';
import { LanguageService } from '../../i18n/language.service';
import { notificationDetailKey } from '../../layout/notification-center';

const PAGE_SIZE = 20;

export type NotificationFilter = 'all' | 'unread';

/** Only whitelisted values are read from the URL; anything else falls back to the first page of all notifications. */
export function parseNotificationQuery(p: ParamMap): { page: number; filter: NotificationFilter } {
  const page = Number(p.get('page'));
  return {
    page: Number.isInteger(page) && page >= 1 ? page : 1,
    filter: p.get('filter') === 'unread' ? 'unread' : 'all',
  };
}

/** Only icons present in the Material Symbols subset requested by index.html. */
const TYPE_ICONS: Record<NotificationType, string> = {
  TicketCreated: 'add_circle',
  TicketAssigned: 'assignment_ind',
  TicketUnassigned: 'person_off',
  TicketStatusChanged: 'sync',
  TicketResolved: 'task_alt',
  TicketClosed: 'check_circle',
  TicketReopened: 'history',
  MessageReceived: 'chat',
  NoteAdded: 'edit_note',
  SlaWarning: 'timer',
  SlaBreached: 'warning',
};

/**
 * Persisted notification history of the signed-in user across all workspaces with an active membership. The page owns its own
 * paginated state (separate from the header dropdown) and never changes read state: loading or viewing it marks nothing as read.
 */
@Component({
  selector: 'app-notification-history',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './notification-history.html',
  styleUrl: './notification-history.css',
})
export class NotificationHistory {
  private readonly notificationsApi = inject(NotificationsService);
  private readonly auth = inject(AuthService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly language = inject(LanguageService);
  /** Shared header state: unread count, read actions and ticket navigation live in the one NotificationService. */
  protected readonly shared = inject(NotificationService);

  protected readonly filters: readonly NotificationFilter[] = ['all', 'unread'];
  protected readonly detailKey = notificationDetailKey;
  protected readonly icon = (n: NotificationDto): string => TYPE_ICONS[n.type];

  protected readonly query = toSignal(this.route.queryParamMap.pipe(map(parseNotificationQuery)), {
    requireSync: true,
  });

  /**
   * A new request replaces (and aborts) the previous one when the page, filter, user or active workspace changes. Results are
   * tagged with their user so a late response of a previous session is never shown.
   */
  protected readonly history = rxResource({
    params: () => {
      const userId = this.auth.user()?.id;
      if (!userId) return undefined;
      return { userId, tenantId: this.tenant.tenantId(), query: this.query() };
    },
    stream: ({ params }) =>
      this.notificationsApi
        .listMyNotifications({
          page: params.query.page,
          pageSize: PAGE_SIZE,
          unreadOnly: params.query.filter === 'unread',
        })
        .pipe(map((page) => ({ userId: params.userId, page }))),
  });

  protected readonly result = computed(() => {
    const r = this.history.hasValue() ? this.history.value() : undefined;
    return r && r.userId === this.auth.user()?.id ? r.page : null;
  });
  protected readonly total = computed(() => Number(this.result()?.totalCount ?? 0));
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / PAGE_SIZE)));
  protected readonly range = computed(() => {
    const from = this.total() ? (this.query().page - 1) * PAGE_SIZE + 1 : 0;
    return { from, to: Math.min(this.total(), this.query().page * PAGE_SIZE), total: this.total() };
  });
  protected readonly pageInfo = computed(() => ({ page: this.query().page, pages: this.pages() }));

  private readonly formatter = computed(
    () =>
      new Intl.DateTimeFormat(this.language.current(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  private seenChanges = this.shared.changes();

  constructor() {
    // A page beyond the end (stale bookmark, or rows were read meanwhile under "Unread") moves to the last existing page.
    effect(() => {
      const r = this.result();
      const last = this.pages();
      if (r && !r.items.length && this.query().page > last) this.goTo(last, true);
    });
    // A read action from any surface, a new live notification (coalesced), a reconnect or another tab refreshes the current SERVER page
    // with the same filter and page: counts, ranges and shifted offsets then match the persisted state without a second copy of the data.
    // reload() keeps the shown rows until the response arrives (no flicker) and cancels a request still in flight.
    effect(() => {
      const changes = this.shared.changes();
      untracked(() => {
        if (changes === this.seenChanges) return;
        this.seenChanges = changes;
        this.history.reload();
      });
    });
    inject(DestroyRef).onDestroy(() => this.shared.actionError.set(null));
  }

  protected readonly hasTicket = hasTicket;

  protected markRead(n: NotificationDto): void {
    this.shared.markRead(n);
  }

  protected openTicket(n: NotificationDto): void {
    void this.shared.openTicket(n);
  }

  protected workspaceName(n: NotificationDto): string {
    return this.tenant.organizations().find((o) => o.id === n.tenantId)?.name ?? '';
  }

  protected when(n: NotificationDto): string {
    const d = new Date(n.createdAtUtc);
    return Number.isNaN(d.getTime()) ? '' : this.formatter().format(d);
  }

  protected setFilter(filter: NotificationFilter): void {
    this.navigate({ filter: filter === 'all' ? null : filter, page: null });
  }

  protected goTo(page: number, replaceUrl = false): void {
    if (page >= 1 && page <= this.pages())
      this.navigate({ page: page === 1 ? null : page }, replaceUrl);
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
