import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Api } from '../api/generated/api';
import {
  getMyUnreadNotificationCount,
  listMyNotifications,
  markAllMyNotificationsRead,
  markNotificationRead,
} from '../api/generated/functions';
import { NotificationDto } from '../api/generated/models';
import { AuthService } from '../auth/auth.service';
import { RealtimeService } from '../realtime/realtime.service';

export interface NotificationCreatedEvent {
  notification: NotificationDto;
}

const PAGE_SIZE = 20;

/**
 * The signed-in user's notification history and unread count across ALL their workspaces. The server restricts both to workspaces where
 * the membership is active, and every item keeps its own tenant id. Live events are addressed to this user only, so they are shown
 * whichever workspace is active; everything resyncs from the server on sign-in and reconnect.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);

  readonly items = signal<NotificationDto[]>([]);
  readonly unread = signal(0);
  readonly loading = signal(false);
  readonly error = signal(false);
  private readonly total = signal(0);
  private page = 1;
  /** Bumped by every resync/reset so a slow response of an earlier request never overwrites newer state. */
  private generation = 0;
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
    this.loading.set(true);
    this.error.set(false);
    this.api.invoke(getMyUnreadNotificationCount, {}).subscribe({
      next: (r) => {
        if (generation === this.generation) this.unread.set(Number(r.count));
      },
      error: () => {
        if (generation === this.generation) this.error.set(true);
      },
    });
    this.api.invoke(listMyNotifications, { page: 1, pageSize: PAGE_SIZE }).subscribe({
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
    this.api.invoke(listMyNotifications, { page: this.page + 1, pageSize: PAGE_SIZE }).subscribe({
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

  /** Uses the notification's own workspace in the route, so it also works while another workspace is active. */
  markRead(n: NotificationDto): void {
    if (n.isRead) return;
    this.api.invoke(markNotificationRead, { tenantId: n.tenantId, notificationId: n.id }).subscribe({
      next: () => {
        this.items.update((list) => list.map((x) => (x.id === n.id ? { ...x, isRead: true } : x)));
        this.unread.update((c) => Math.max(0, c - 1));
      },
      error: () => this.error.set(true),
    });
  }

  markAllRead(): void {
    this.api.invoke(markAllMyNotificationsRead, {}).subscribe({
      next: () => {
        this.items.update((list) => list.map((x) => ({ ...x, isRead: true })));
        this.unread.set(0);
      },
      error: () => this.error.set(true),
    });
  }

  private reset(): void {
    this.generation++;
    this.items.set([]);
    this.unread.set(0);
    this.total.set(0);
    this.loading.set(false);
    this.error.set(false);
    this.page = 1;
  }
}
