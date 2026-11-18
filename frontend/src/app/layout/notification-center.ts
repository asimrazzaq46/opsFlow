import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ConfirmDialog } from '../shared/components/confirm-dialog/confirm-dialog';
import { NotificationDto } from '../core/api/generated/models';
import { TenantService } from '../core/auth/tenant.service';
import { NotificationService, hasTicket } from '../core/notifications/notification.service';

/** SLA notifications carry an SLA kind (FirstResponse/Resolution) as detail, every other type a ticket status. */
export const notificationDetailKey = (n: Pick<NotificationDto, 'type' | 'detail'>): string =>
  (n.type === 'SlaWarning' || n.type === 'SlaBreached'
    ? 'notifications.slaKinds.'
    : 'tickets.status.') + (n.detail ?? '');

/** One bell for the user's notifications from every workspace; opening one switches to its own workspace first. */
@Component({
  selector: 'app-notification-center',
  imports: [TranslatePipe, DatePipe, RouterLink, ConfirmDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'relative',
    '(keydown.escape)': 'open.set(false)',
    '(document:click)': 'closeOnOutsideClick($event)',
  },
  templateUrl: './notification-center.html',
  styleUrl: './notification-center.css',
})
export class NotificationCenter {
  protected readonly notifications = inject(NotificationService);
  private readonly tenant = inject(TenantService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly open = signal(false);

  protected readonly detailKey = notificationDetailKey;
  protected readonly hasTicket = hasTicket;
  /** The one cross-workspace confirmation for the dropdown AND the /notifications page (this component is always in the shell). */
  private readonly switchDialog = viewChild.required<ConfirmDialog>('switchDialog');

  constructor() {
    effect(() => {
      const pending = this.notifications.pendingSwitch();
      untracked(() => (pending ? this.switchDialog().open() : this.switchDialog().close()));
    });
  }

  protected toggle(): void {
    this.notifications.actionError.set(null);
    this.open.update((v) => !v);
  }

  /**
   * The bell, the panel and the switch dialog all live inside this host, so only clicks elsewhere close the panel. composedPath() is fixed at
   * dispatch, so a clicked element that re-renders away still counts as inside. pendingSwitch is untouched: the dialog is outside the panel.
   */
  protected closeOnOutsideClick(event: MouseEvent): void {
    if (this.open() && !event.composedPath().includes(this.host.nativeElement))
      this.open.set(false);
  }

  protected workspaceName(n: NotificationDto): string {
    return this.tenant.organizations().find((o) => o.id === n.tenantId)?.name ?? '';
  }

  /**
   * The service verifies the ticket with the notification's own tenant before anything changes: only a successfully opened ticket
   * switches the workspace and marks the notification read; failures are reported in the panel.
   */
  protected async go(n: NotificationDto): Promise<void> {
    if (await this.notifications.openTicket(n)) this.open.set(false);
  }

  /** The dialog closes before navigating, so a page's own unsaved-changes prompt follows it instead of overlapping it. */
  protected async confirmSwitch(): Promise<void> {
    const opened = this.notifications.confirmSwitch();
    this.switchDialog().close();
    if (await opened) this.open.set(false);
  }
}
