import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, debounceTime, filter, merge } from 'rxjs';
import { TicketDetailDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { NotificationCreatedEvent } from '../../core/notifications/notification.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { activeWorkspaceEvents } from '../../core/realtime/workspace-events';
import { minutesLabel } from '../../shared/format';
import { TicketActions } from './ticket-actions';
import { TicketActivity } from './ticket-activity';
import { TicketAttachments } from './ticket-attachments';
import { TicketChat } from './ticket-chat';
import { TicketEdit } from './ticket-edit';
import { TicketNotes } from './ticket-notes';
import { TicketTasks } from './ticket-tasks';
import { PRIORITY_BADGE, STATUS_BADGE, httpStatus } from './ticket-ui';
import { TicketsApi } from './tickets-api';

export type DetailTab = 'conversation' | 'notes' | 'attachments' | 'tasks' | 'activity';

// Notifications that mean the ticket header (status, staffing, SLA) changed on the server.
const TICKET_CHANGES = new Set([
  'TicketAssigned',
  'TicketUnassigned',
  'TicketStatusChanged',
  'TicketResolved',
  'TicketClosed',
  'TicketReopened',
]);

/** Customers may edit only title and description of their open tickets; nobody edits a closed one (the server enforces both). */
export function canEditTicket(t: TicketDetailDto, staff: boolean): boolean {
  return t.summary.status !== 'Closed' && (staff || t.summary.status === 'Open');
}

@Component({
  selector: 'app-ticket-detail',
  imports: [
    RouterLink,
    TranslatePipe,
    DatePipe,
    TicketChat,
    TicketNotes,
    TicketAttachments,
    TicketActions,
    TicketTasks,
    TicketActivity,
    TicketEdit,
  ],
  templateUrl: './ticket-detail.html',
  styleUrl: './ticket-detail.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketDetail {
  readonly id = input.required<string>();

  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly router = inject(Router);

  protected readonly statusBadge = STATUS_BADGE;
  protected readonly priorityBadge = PRIORITY_BADGE;
  /** Remaining time is computed by the server at read time; the browser only formats it. */
  protected readonly remaining = (m: number | string | null) =>
    m === null ? null : minutesLabel(Number(m));
  protected readonly ticket = signal<TicketDetailDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly tab = signal<DetailTab>('conversation');
  /** Tabs are mounted on first visit and then kept, so their realtime subscriptions and drafts survive tab switches. */
  protected readonly visited = signal<ReadonlySet<DetailTab>>(new Set(['conversation']));
  protected readonly tabs = computed<{ key: DetailTab; icon: string; label: string }[]>(() => [
    { key: 'conversation', icon: 'chat', label: 'chat.title' },
    ...(this.tenant.isStaff()
      ? [{ key: 'notes' as const, icon: 'lock', label: 'notes.title' }]
      : []),
    { key: 'attachments', icon: 'attachment', label: 'attachments.title' },
    ...(this.tenant.isStaff()
      ? [{ key: 'tasks' as const, icon: 'task_alt', label: 'tasks.title' }]
      : []),
    { key: 'activity', icon: 'timeline', label: 'tickets.activity.title' },
  ]);
  /** Agents see every ticket of the workspace but work only on those assigned to them; the server enforces it, the UI just says so. */
  protected readonly unassignedAgent = computed(
    () => this.tenant.role() === 'Agent' && this.ticket()?.summary.assignedToMe === false,
  );
  protected readonly canEdit = computed(() => {
    const t = this.ticket();
    return !!t && !this.unassignedAgent() && canEditTicket(t, this.tenant.isStaff());
  });

  /** Workspace the shown ticket belongs to; follows the ticket when a notification opens another one in a different workspace. */
  private openedIn = this.tenant.tenantId();
  private request?: Subscription;

  constructor() {
    effect(() => {
      // a ticket belongs to ONE workspace: switching workspaces leaves the page instead of showing stale data
      // A navigation already under way (e.g. opening a notification of another workspace) decides where the user lands.
      if (this.tenant.tenantId() !== this.openedIn && !this.router.getCurrentNavigation())
        void this.router.navigateByUrl('/tickets');
    });
    effect(() => {
      this.id();
      untracked(() => {
        this.openedIn = this.tenant.tenantId();
        this.load();
      });
    });
    const realtime = inject(RealtimeService);
    merge(
      realtime
        .on<NotificationCreatedEvent>('notification.created')
        .pipe(
          filter(
            (e) =>
              e.notification.tenantId === this.tenant.tenantId() &&
              e.notification.ticketId === this.id() &&
              TICKET_CHANGES.has(e.notification.type),
          ),
        ),
      // Workspace staff who are not recipients of the personal notification still learn that this ticket changed.
      activeWorkspaceEvents<{ tenantId: string; ticketId: string }>('ticket.changed').pipe(filter((e) => e.ticketId === this.id())),
      realtime.reconnected,
    )
      .pipe(debounceTime(500), takeUntilDestroyed())
      .subscribe(() => this.refresh());
  }

  protected select(tab: DetailTab): void {
    this.tab.set(tab);
    this.visited.update((v) => new Set(v).add(tab));
  }

  /** Arrow keys move between tabs (WAI-ARIA tabs pattern with automatic activation). */
  protected onTabKey(event: KeyboardEvent, index: number): void {
    const tabs = this.tabs();
    const next =
      event.key === 'ArrowRight'
        ? index + 1
        : event.key === 'ArrowLeft'
          ? index - 1
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? tabs.length - 1
              : null;
    if (next === null) return;
    event.preventDefault();
    const target = tabs[(next + tabs.length) % tabs.length];
    this.select(target.key);
    document.getElementById('tab-' + target.key)?.focus();
  }

  /** Silent reload after a command or a live event: keeps the mounted tabs and their state. */
  protected refresh(): void {
    if (!this.ticket()) return;
    this.request?.unsubscribe();
    this.request = this.api
      .get(this.id())
      .subscribe({ next: (t) => this.ticket.set(t), error: () => undefined });
  }

  protected saved(t: TicketDetailDto): void {
    this.ticket.set(t);
    this.editing.set(false);
  }

  protected load(): void {
    this.request?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    this.request = this.api.get(this.id()).subscribe({
      next: (t) => {
        this.ticket.set(t);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.ticket.set(null);
        const status = httpStatus(e);
        // 404 also covers tickets of other workspaces or outside the caller's visibility: nothing about them is revealed
        this.error.set(
          status === 404
            ? 'tickets.detail.notFound'
            : status === 403
              ? 'common.forbidden'
              : 'tickets.loadError',
        );
        this.loading.set(false);
      },
    });
  }
}
