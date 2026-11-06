import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { NotificationDto } from '../core/api/generated/models';
import { TenantService } from '../core/auth/tenant.service';
import { NotificationService } from '../core/notifications/notification.service';

/** SLA notifications carry an SLA kind (FirstResponse/Resolution) as detail, every other type a ticket status. */
export const notificationDetailKey = (n: Pick<NotificationDto, 'type' | 'detail'>): string =>
  (n.type === 'SlaWarning' || n.type === 'SlaBreached'
    ? 'notifications.slaKinds.'
    : 'tickets.status.') + (n.detail ?? '');

/** One bell for the user's notifications from every workspace; opening one switches to its own workspace first. */
@Component({
  selector: 'app-notification-center',
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'relative', '(keydown.escape)': 'open.set(false)' },
  templateUrl: './notification-center.html',
  styleUrl: './notification-center.css',
})
export class NotificationCenter {
  protected readonly notifications = inject(NotificationService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);
  protected readonly open = signal(false);
  /** Set when a notification belongs to a workspace the user can no longer open. */
  protected readonly blocked = signal(false);

  protected readonly detailKey = notificationDetailKey;

  protected toggle(): void {
    this.blocked.set(false);
    this.open.update((v) => !v);
  }

  protected workspaceName(n: NotificationDto): string {
    return this.tenant.organizations().find((o) => o.id === n.tenantId)?.name ?? '';
  }

  /**
   * The notification's tenant comes from the server row, never from the active workspace. Access is confirmed against the memberships
   * the server lists; the ticket itself is authorized again by its own page, which handles a missing or inaccessible ticket.
   */
  protected async go(n: NotificationDto): Promise<void> {
    this.blocked.set(false);
    if (!this.tenant.organizations().some((o) => o.id === n.tenantId)) {
      try {
        await firstValueFrom(this.tenant.load()); // memberships may have changed since they were loaded
      } catch {
        // falls through to the access check below
      }
    }

    if (!this.tenant.select(n.tenantId)) {
      this.blocked.set(true);
      return;
    }

    this.notifications.markRead(n);
    this.open.set(false);
    await this.router.navigate(['/tickets', n.ticketId]);
  }
}
