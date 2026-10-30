import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { NotificationDto } from '../core/api/generated/models';
import { NotificationService } from '../core/notifications/notification.service';

@Component({
  selector: 'app-notification-center',
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'relative', '(keydown.escape)': 'open.set(false)' },
  template: `
    <button type="button" aria-haspopup="true" aria-controls="notification-panel" [attr.aria-expanded]="open()"
      class="relative grid size-9 place-items-center rounded-lg border border-border text-muted hover:text-fg focus-visible:outline-2 focus-visible:outline-brand"
      [attr.aria-label]="'notifications.bell' | translate: { count: notifications.unread() }" (click)="toggle()">
      <span aria-hidden="true">🔔</span>
      @if (notifications.unread() > 0) {
        <span class="absolute -right-1.5 -top-1.5 min-w-5 rounded-full bg-danger px-1 text-center text-xs font-semibold text-white" aria-hidden="true">
          {{ notifications.unread() > 99 ? '99+' : notifications.unread() }}
        </span>
      }
    </button>
    <span class="sr-only" aria-live="polite">{{ 'notifications.unreadCount' | translate: { count: notifications.unread() } }}</span>

    @if (open()) {
      <div id="notification-panel" role="region" [attr.aria-label]="'notifications.title' | translate"
        class="absolute right-0 z-20 mt-2 w-[min(22rem,calc(100vw-2rem))] rounded-xl border border-border bg-card p-3 shadow-lg">
        <div class="flex items-center justify-between">
          <h2 class="font-semibold">{{ 'notifications.title' | translate }}</h2>
          <button type="button" class="text-sm underline disabled:opacity-50" [disabled]="notifications.unread() === 0" (click)="notifications.markAllRead()">
            {{ 'notifications.markAll' | translate }}
          </button>
        </div>
        <div class="mt-2 max-h-96 overflow-y-auto" aria-live="polite">
          @if (notifications.error()) {
            <p role="alert" class="text-sm text-danger">{{ 'notifications.error' | translate }}
              <button type="button" class="ml-1 underline" (click)="notifications.resync()">{{ 'common.retry' | translate }}</button></p>
          } @else if (notifications.loading() && !notifications.items().length) {
            <p class="text-sm text-muted">{{ 'common.loading' | translate }}</p>
          } @else if (!notifications.items().length) {
            <p class="text-sm text-muted">{{ 'notifications.empty' | translate }}</p>
          }
          <ul class="divide-y divide-border">
            @for (n of notifications.items(); track n.id) {
              <li>
                <button type="button" class="block w-full px-2 py-2 text-left hover:bg-surface focus-visible:outline-2 focus-visible:outline-brand" (click)="go(n)">
                  <span class="flex items-start gap-2">
                    @if (!n.isRead) {
                      <span class="mt-1.5 size-2 shrink-0 rounded-full bg-brand"></span>
                      <span class="sr-only">{{ 'notifications.unread' | translate }}</span>
                    }
                    <span class="min-w-0 text-sm" [class.font-semibold]="!n.isRead">
                      {{ 'notifications.types.' + n.type | translate: { subject: n.subject } }}
                      @if (n.detail) {
                        <span class="text-muted">({{ 'tickets.status.' + n.detail | translate }})</span>
                      }
                      <time class="block text-xs text-muted" [attr.datetime]="n.createdAtUtc">{{ n.createdAtUtc | date: 'short' }}</time>
                    </span>
                  </span>
                </button>
              </li>
            }
          </ul>
          @if (notifications.hasMore()) {
            <button type="button" class="mt-2 w-full text-sm underline" [disabled]="notifications.loading()" (click)="notifications.loadMore()">
              {{ 'notifications.loadMore' | translate }}
            </button>
          }
        </div>
      </div>
    }
  `,
})
export class NotificationCenter {
  protected readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);
  protected readonly open = signal(false);

  protected toggle(): void {
    this.open.update((v) => !v);
  }

  protected go(n: NotificationDto): void {
    this.notifications.markRead(n);
    this.open.set(false);
    void this.router.navigate(['/tickets', n.ticketId]);
  }
}
