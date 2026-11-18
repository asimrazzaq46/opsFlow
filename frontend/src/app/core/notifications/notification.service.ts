import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { finalize, firstValueFrom } from 'rxjs';
import { NotificationDto } from '../api/generated/models';
import { NotificationsService, TicketsService } from '../api/generated/services';
import { AuthService } from '../auth/auth.service';
import { TenantService } from '../auth/tenant.service';
import { RealtimeService } from '../realtime/realtime.service';

export interface NotificationCreatedEvent {
  notification: NotificationDto;
}

const PAGE_SIZE = 20;
const EMPTY_ID = '00000000-0000-0000-0000-000000000000';

/** A notification links to a ticket only when it carries a real ticket id. */
export const hasTicket = (n: Pick<NotificationDto, 'ticketId'>): boolean => !!n.ticketId && n.ticketId !== EMPTY_ID;

/**
 * The signed-in user's notification history and unread count across ALL their workspaces. The server restricts both to workspaces where
 * the membership is active, and every item keeps its own tenant id. Live events are addressed to this user only, so they are shown
 * whichever workspace is active; everything resyncs from the server on sign-in and reconnect.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly notificationsApi = inject(NotificationsService);
  private readonly ticketsApi = inject(TicketsService);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  readonly items = signal<NotificationDto[]>([]);
  readonly unread = signal(0);
  readonly loading = signal(false);
  /** A failed LOAD of the history or count; failed actions use `actionError` so a loaded list is never replaced by an error screen. */
  readonly error = signal(false);
  /** Translation key of the last failed read or open action; cleared when the next action starts. */
  readonly actionError = signal<string | null>(null);
  private readonly _readBusy = signal<ReadonlySet<string>>(new Set());
  /** Ids with a mark-read request in flight (duplicate clicks are ignored). */
  readonly readBusy = this._readBusy.asReadonly();
  readonly markingAll = signal(false);
  /** Id of the notification whose ticket is being opened. */
  readonly opening = signal<string | null>(null);
  /** Bumped after every confirmed read mutation so other views of the history (the /notifications page) refresh their server page. */
  readonly readChanges = signal(0);
  private readonly total = signal(0);
  private page = 1;
  /** A ticket of another workspace waiting for the user's switch confirmation; one at a time, shown by the shell's NotificationCenter. */
  readonly pendingSwitch = signal<{ notification: NotificationDto; current: string; target: string } | null>(null);
  /** Bumped by every resync/reset so a slow response of an earlier request never overwrites newer state. */
  private generation = 0;
  /** Bumped only by sign-in/sign-out resets: an action response of a previous user is dropped, while a reconnect resync does not discard it. */
  private session = 0;
  /** Only the newest count request may update the badge, whatever order the responses arrive in. */
  private countRequest = 0;
  readonly hasMore = computed(() => this.items().length < this.total());

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.realtime.on<NotificationCreatedEvent>('notification.created').pipe(takeUntilDestroyed(destroyRef)).subscribe((e) => this.receive(e));
    this.realtime.reconnected.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.resync());
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => {
        this.reset();
        if (signedIn) this.resync();
      });
    });
  }

  receive(e: NotificationCreatedEvent): void {
    const n = e.notification;
    if (this.items().some((x) => x.id === n.id)) return;
    this.items.update((list) => [n, ...list]);
    this.total.update((t) => t + 1);
    if (!n.isRead) this.unread.update((c) => c + 1);
  }

  resync(): void {
    if (!this.auth.isAuthenticated()) return;
    const generation = ++this.generation;
    this.countRequest++;
    this.loading.set(true);
    this.error.set(false);
    this.notificationsApi.getMyUnreadNotificationCount().subscribe({
      next: (r) => {
        if (generation === this.generation) this.unread.set(Number(r.count));
      },
      error: () => {
        if (generation === this.generation) this.error.set(true);
      },
    });
    this.notificationsApi.listMyNotifications({ page: 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (generation !== this.generation) return;
        this.items.set(r.items);
        this.total.set(Number(r.totalCount));
        this.page = 1;
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  loadMore(): void {
    if (this.loading() || !this.auth.isAuthenticated()) return;
    const generation = this.generation;
    this.loading.set(true);
    this.notificationsApi.listMyNotifications({ page: this.page + 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (generation !== this.generation) return;
        const known = new Set(this.items().map((x) => x.id));
        this.items.update((list) => [...list, ...r.items.filter((x) => !known.has(x.id))]);
        this.page++;
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  /**
   * Uses the notification's own workspace in the route, so it also works while another workspace is active. The badge changes only
   * after the server confirmed the update and is then settled against the persisted count.
   */
  markRead(n: NotificationDto): void {
    if (n.isRead || this._readBusy().has(n.id)) return;
    const local = this.items().find((x) => x.id === n.id);
    if (local?.isRead) {
      this.readChanges.update((v) => v + 1); // the caller's copy is stale: let it refresh
      return;
    }

    this.actionError.set(null);
    this._readBusy.update((s) => new Set(s).add(n.id));
    const session = this.session;
    this.notificationsApi
      .markNotificationRead({ tenantId: n.tenantId, notificationId: n.id })
      .pipe(
        finalize(() =>
          this._readBusy.update((s) => {
            const next = new Set(s);
            next.delete(n.id);
            return next;
          }),
        ),
      )
      .subscribe({
        next: () => {
          if (session !== this.session) return;
          this.items.update((list) => list.map((x) => (x.id === n.id ? { ...x, isRead: true } : x)));
          if (local) this.unread.update((c) => Math.max(0, c - 1));
          this.readChanges.update((v) => v + 1);
          this.refreshUnread();
        },
        error: () => {
          if (session === this.session) this.actionError.set('notifications.errors.read');
        },
      });
  }

  /** Cross-workspace on the server: every unread notification of the user in all active workspaces, not only the selected one. */
  markAllRead(): void {
    if (this.markingAll()) return;
    this.actionError.set(null);
    this.markingAll.set(true);
    const session = this.session;
    this.notificationsApi
      .markAllMyNotificationsRead()
      .subscribe({
        next: () => {
          if (session !== this.session) return;
          this.markingAll.set(false);
          this.items.update((list) => list.map((x) => ({ ...x, isRead: true })));
          this.unread.set(0);
          this.readChanges.update((v) => v + 1);
          this.resync(); // anything that arrived while the request ran is still unread on the server
        },
        error: () => {
          if (session !== this.session) return;
          this.markingAll.set(false);
          this.actionError.set('notifications.errors.readAll');
        },
      });
  }

  /**
   * Opens the notification's ticket. The ticket is first requested with the notification's OWN tenant, so a missing or inaccessible
   * ticket neither switches workspace nor marks anything read. The workspace becomes active only inside the navigation (see
   * `notificationWorkspaceGuard`), i.e. after an unsaved-changes prompt was accepted. Resolves true when the ticket page was opened.
   */
  async openTicket(n: NotificationDto, switchConfirmed = false): Promise<boolean> {
    if (this.opening() !== null || this.pendingSwitch() !== null) return false;
    const session = this.session;
    this.actionError.set(null);
    if (!hasTicket(n)) return this.fail('notifications.errors.ticketUnavailable');
    this.opening.set(n.id);
    try {
      if (!this.tenant.organizations().some((o) => o.id === n.tenantId)) {
        try {
          await firstValueFrom(this.tenant.load()); // memberships may have changed since they were loaded
        } catch {
          // falls through to the access check below
        }
      }

      if (session !== this.session) return false;
      if (!this.tenant.organizations().some((o) => o.id === n.tenantId)) return this.fail('notifications.inaccessible');
      try {
        await firstValueFrom(this.ticketsApi.getTicket({ tenantId: n.tenantId, ticketId: n.ticketId }));
      } catch (e) {
        const status = (e as { status?: number }).status;
        return this.fail(status === 403 || status === 404 ? 'notifications.errors.ticketUnavailable' : 'notifications.errors.ticketFailed');
      }

      if (session !== this.session) return false;
      const current = this.tenant.tenantId();
      if (!switchConfirmed && current !== n.tenantId) {
        // Another workspace: nothing changes until the user confirms in the shell's dialog (names come from the server's membership list).
        const name = (id: string | null) => this.tenant.organizations().find((o) => o.id === id)?.name ?? '';
        this.pendingSwitch.set({ notification: n, current: name(current), target: name(n.tenantId) });
        return false;
      }

      const target = `/tickets/${n.ticketId}`;
      const onTicket = () => this.router.url.split(/[?#]/)[0] === target;
      let opened = onTicket();
      if (!opened) {
        let navigated: boolean;
        try {
          navigated = await this.router.navigate(['/tickets', n.ticketId], { state: { notificationTenantId: n.tenantId } });
        } catch {
          return this.fail('notifications.errors.ticketFailed'); // a failed navigation is reported, never swallowed
        }

        if (session !== this.session) return false; // signed out meanwhile: never mark the previous user's notification
        opened = navigated && onTicket();
        // false = cancelled (e.g. unsaved-changes prompt declined): nothing to report. A guard redirect lands elsewhere and is reported.
        if (navigated && !opened) return this.fail('notifications.errors.ticketFailed');
      }

      if (opened) this.markRead(n);
      return opened;
    } finally {
      this.opening.set(null);
    }
  }

  /** The user confirmed the workspace switch: the whole check (membership, ticket access) runs again before navigating. */
  confirmSwitch(): Promise<boolean> {
    const pending = this.pendingSwitch();
    this.pendingSwitch.set(null);
    return pending ? this.openTicket(pending.notification, true) : Promise.resolve(false);
  }

  /** Cancel or Escape: nothing was changed, so nothing needs undoing. */
  cancelSwitch(): void {
    this.pendingSwitch.set(null);
  }

  private fail(key: string): false {
    this.actionError.set(key);
    return false;
  }

  private refreshUnread(): void {
    const session = this.session;
    const request = ++this.countRequest;
    this.notificationsApi.getMyUnreadNotificationCount().subscribe({
      next: (r) => {
        if (session === this.session && request === this.countRequest) this.unread.set(Number(r.count));
      },
      error: () => undefined, // the local value stays; the next resync corrects it
    });
  }

  private reset(): void {
    this.generation++;
    this.session++;
    this.countRequest++;
    this.items.set([]);
    this.unread.set(0);
    this.total.set(0);
    this.loading.set(false);
    this.error.set(false);
    this.actionError.set(null);
    this._readBusy.set(new Set());
    this.markingAll.set(false);
    this.opening.set(null);
    this.pendingSwitch.set(null);
    this.page = 1;
  }
}
