import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { MemberDto, TicketDetailDto, TicketStatus } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { TicketSlaHistoryDocument, TicketSlaHistoryQuery } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';
import { initials } from '../workspaces/workspace-api';
import { TicketsApi } from './tickets-api';

// Mirrors the backend workflow only to decide which buttons to show; the server validates every command.
const NEXT: Record<TicketStatus, TicketStatus[]> = {
  Open: [],
  Assigned: ['InProgress', 'Resolved'],
  InProgress: ['OnHold', 'Resolved'],
  OnHold: ['InProgress', 'Resolved'],
  Resolved: ['Closed'],
  Closed: [],
};

@Component({
  selector: 'app-ticket-actions',
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ticket-actions.html',
  styleUrl: './ticket-actions.css',
})
export class TicketActions {
  readonly ticket = input.required<TicketDetailDto>();
  readonly changed = output<void>();

  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly gql = inject(GraphqlClient);

  protected readonly pascal = pascal;
  protected readonly initials = initials;
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly members = signal<MemberDto[]>([]);
  protected readonly cycles = signal<TicketSlaHistoryQuery['ticketSla']['cycles'] | null>(null);

  protected readonly isManager = computed(
    () => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager',
  );
  /** Mirrors TicketWorkflowService: Agents act only on tickets assigned to them and never close; the server still decides. */
  protected readonly next = computed(() => {
    const summary = this.ticket().summary;
    if (this.isManager()) return NEXT[summary.status];
    if (!this.tenant.isStaff() || !summary.assignedToMe) return [];
    return NEXT[summary.status].filter((s) => s !== 'Closed');
  });
  /** Reopen starts a new SLA cycle on the server. Customers may reopen only a resolved ticket of their own. */
  protected readonly canReopen = computed(() => {
    const { status, assignedToMe } = this.ticket().summary;
    if (status !== 'Resolved' && status !== 'Closed') return false;
    if (this.isManager()) return true;
    if (!this.tenant.isStaff()) return status === 'Resolved';
    return !!assignedToMe;
  });
  /**
   * An active Agent may claim a ticket they are not assigned to while it is still workable (the server rejects finished tickets and
   * re-checks eligibility). The own membership id comes from the server-listed workspace, never from user input.
   */
  protected readonly canClaim = computed(() => {
    const { status, assignedToMe } = this.ticket().summary;
    return (
      this.tenant.role() === 'Agent' &&
      !!this.tenant.current()?.membershipId &&
      assignedToMe === false &&
      status !== 'Resolved' &&
      status !== 'Closed'
    );
  });
  protected readonly assignable = computed(() => {
    const taken = new Set(this.ticket().assignees?.map((a) => a.membershipId));
    return this.members().filter((m) => m.role === 'Agent' && m.isActive && !taken.has(m.id));
  });

  constructor() {
    effect(() => {
      if (this.isManager())
        untracked(() => this.api.members().subscribe((m) => this.members.set(m)));
    });
    effect(() => {
      const id = this.ticket().summary.id;
      this.ticket().summary.version; // re-read the cycle history after every change (a reopen adds a cycle)
      if (this.tenant.isStaff())
        untracked(() =>
          this.gql
            .query(TicketSlaHistoryDocument, { ticketId: id })
            .subscribe({
              next: (r) => this.cycles.set(r.ticketSla.cycles),
              error: () => this.cycles.set([]),
            }),
        );
    });
  }

  protected move(status: TicketStatus): void {
    this.run(
      this.api.changeStatus(
        this.ticket().summary.id,
        status,
        Number(this.ticket().summary.version),
      ),
    );
  }

  protected reopen(): void {
    this.run(this.api.reopen(this.ticket().summary.id, Number(this.ticket().summary.version)));
  }

  protected assign(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const id = select.value;
    select.value = '';
    if (id)
      this.run(
        this.api.assign(this.ticket().summary.id, [id], Number(this.ticket().summary.version)),
      );
  }

  protected claim(): void {
    const me = this.tenant.current()?.membershipId;
    if (me) this.run(this.api.assign(this.ticket().summary.id, [me], Number(this.ticket().summary.version)));
  }

  protected unassign(membershipId: string): void {
    this.run(
      this.api.unassign(
        this.ticket().summary.id,
        membershipId,
        Number(this.ticket().summary.version),
      ),
    );
  }

  private run(call: ReturnType<TicketsApi['changeStatus']>): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        this.changed.emit();
      },
      error: (e: unknown) => {
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        // Unassign is rejected while the agent still owns unfinished child tasks; show a specific message
        const code =
          e instanceof HttpErrorResponse ? (e.error as { code?: string } | null)?.code : undefined;
        this.message.set(
          code === 'TICKET_AGENT_HAS_OPEN_TASKS'
            ? 'tasks.errors.agentHasOpenTasks'
            : status === 409
              ? 'tickets.errors.conflict'
              : status === 403
                ? 'common.forbidden'
                : status === 404
                  ? 'tickets.detail.notFound'
                  : status === 400
                    ? 'tickets.errors.invalid'
                    : 'tickets.errors.generic',
        );
        this.busy.set(false);
        this.changed.emit(); // reload the authoritative state (never retry automatically)
      },
    });
  }
}
