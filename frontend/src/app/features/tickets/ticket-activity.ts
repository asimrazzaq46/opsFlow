import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslatePipe } from '@ngx-translate/core';
import { TicketActivityDto, TicketActivityType } from '../../core/api/generated/models';
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

/** Read-only audit timeline (newest first). It reloads when the ticket version changes, i.e. after any recorded mutation. */
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

  private readonly api = inject(TicketsApi);
  protected readonly activity = rxResource({
    params: () => ({ id: this.ticketId(), version: this.version() }),
    stream: ({ params }) => this.api.activity(params.id),
  });
  protected readonly entries = computed(() => [...(this.activity.value()?.items ?? [])].reverse());
  protected readonly truncated = computed(() => {
    const p = this.activity.value();
    return !!p && Number(p.totalCount) > p.items.length;
  });

  protected icon = (a: TicketActivityDto) => ICONS[a.type] ?? 'history';
  protected valueKey = (a: TicketActivityDto, v: string | null) => (v && VALUE_PREFIX[a.type] ? VALUE_PREFIX[a.type] + v : null);
}
