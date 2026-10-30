import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Api } from '../api/generated/api';
import { getUnreadNotificationCount, listNotifications, markAllNotificationsRead, markNotificationRead } from '../api/generated/functions';
import { NotificationDto } from '../api/generated/models';
import { TenantService } from '../auth/tenant.service';
import { RealtimeService } from '../realtime/realtime.service';

export interface NotificationCreatedEvent {
  notification: NotificationDto;
}

const PAGE_SIZE = 20;

/** Notification history + unread count of the ACTIVE tenant. Live events of other tenants are ignored; everything resyncs from the server on tenant switch and reconnect. */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly api = inject(Api);
  private readonly tenant = inject(TenantService);
  private readonly realtime = inject(RealtimeService);

  readonly items = signal<NotificationDto[]>([]);
  readonly unread = signal(0);
  readonly loading = signal(false);
  readonly error = signal(false);
  private readonly total = signal(0);
  private page = 1;
  readonly hasMore = computed(() => this.items().length < this.total());

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.realtime.on<NotificationCreatedEvent>('notification.created').pipe(takeUntilDestroyed(destroyRef)).subscribe((e) => this.receive(e));
    this.realtime.reconnected.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.resync());
    effect(() => {
      const tenantId = this.tenant.tenantId();
      untracked(() => {
        this.reset();
        if (tenantId) this.resync();
      });
    });
  }

  receive(e: NotificationCreatedEvent): void {
    const n = e.notification;
    if (n.tenantId !== this.tenant.tenantId() || this.items().some((x) => x.id === n.id)) return;
    this.items.update((list) => [n, ...list]);
    this.total.update((t) => t + 1);
    if (!n.isRead) this.unread.update((c) => c + 1);
  }

  resync(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId) return;
    this.loading.set(true);
    this.error.set(false);
    this.api.invoke(getUnreadNotificationCount, { tenantId }).subscribe({
      next: (r) => {
        if (this.tenant.tenantId() === tenantId) this.unread.set(Number(r.count));
      },
      error: () => this.error.set(true),
    });
    this.api.invoke(listNotifications, { tenantId, page: 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (this.tenant.tenantId() !== tenantId) return; // tenant switched meanwhile
        this.items.set(r.items);
        this.total.set(Number(r.totalCount));
        this.page = 1;
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  loadMore(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || this.loading()) return;
    this.loading.set(true);
    this.api.invoke(listNotifications, { tenantId, page: this.page + 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (this.tenant.tenantId() !== tenantId) return;
        const known = new Set(this.items().map((x) => x.id));
        this.items.update((list) => [...list, ...r.items.filter((x) => !known.has(x.id))]);
        this.page++;
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  markRead(n: NotificationDto): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || n.isRead) return;
    this.api.invoke(markNotificationRead, { tenantId, notificationId: n.id }).subscribe({
      next: () => {
        this.items.update((list) => list.map((x) => (x.id === n.id ? { ...x, isRead: true } : x)));
        this.unread.update((c) => Math.max(0, c - 1));
      },
      error: () => this.error.set(true),
    });
  }

  markAllRead(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId) return;
    this.api.invoke(markAllNotificationsRead, { tenantId }).subscribe({
      next: () => {
        this.items.update((list) => list.map((x) => ({ ...x, isRead: true })));
        this.unread.set(0);
      },
      error: () => this.error.set(true),
    });
  }

  private reset(): void {
    this.items.set([]);
    this.unread.set(0);
    this.total.set(0);
    this.error.set(false);
    this.page = 1;
  }
}
