import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { MemberDto, TicketDetailDto, TicketStatus } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { TicketSlaHistoryDocument, TicketSlaHistoryQuery } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';
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
  template: `
    @if (message(); as m) { <p role="alert" class="mb-3 rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ m | translate }}</p> }

    @if (tenant.isStaff() && next().length) {
      <div class="mb-4 flex flex-wrap items-center gap-2" role="group" [attr.aria-label]="'tickets.actions.status' | translate">
        @for (s of next(); track s) {
          <button type="button" [disabled]="busy()" (click)="move(s)" class="rounded-lg border border-border px-3 py-1.5 text-sm hover:border-brand focus-visible:outline-2 focus-visible:outline-brand disabled:opacity-50">
            {{ 'tickets.status.' + s | translate }}</button>
        }
      </div>
    }
    @if (canReopen()) {
      <button type="button" [disabled]="busy()" (click)="reopen()" class="mb-4 rounded-lg border border-border px-3 py-1.5 text-sm hover:border-brand focus-visible:outline-2 focus-visible:outline-brand disabled:opacity-50">
        {{ 'tickets.actions.reopen' | translate }}</button>
    }

    @if (ticket().assignees; as assignees) {
      <section class="mb-4 rounded-xl border border-border bg-card p-4">
        <h2 class="mb-2 font-semibold">{{ 'tickets.assignees' | translate }}</h2>
        <ul class="flex flex-wrap gap-2">
          @for (a of assignees; track a.membershipId) {
            <li class="flex items-center gap-1 rounded-full bg-surface px-3 py-1 text-sm">{{ a.displayName }}
              @if (isManager()) {
                <button type="button" class="ml-1 text-muted hover:text-danger focus-visible:outline-2 focus-visible:outline-brand" [disabled]="busy()" [attr.aria-label]="('tickets.actions.unassign' | translate) + ' ' + a.displayName" (click)="unassign(a.membershipId)">✕</button>
              }
            </li>
          } @empty { <li class="text-sm text-muted">{{ 'tickets.unassigned' | translate }}</li> }
        </ul>
        @if (isManager()) {
          <label class="mt-3 grid gap-1 text-sm"><span class="text-muted">{{ 'tickets.actions.assign' | translate }}</span>
            <select class="max-w-xs rounded-lg border border-border bg-card px-2 py-1" [disabled]="busy()" (change)="assign($event)">
              <option value="">—</option>
              @for (m of assignable(); track m.id) { <option [value]="m.id">{{ m.displayName }}</option> }
            </select></label>
        }
      </section>
    }

    @if (tenant.isStaff()) {
      <section class="mb-4 rounded-xl border border-border bg-card p-4">
        <h2 class="mb-2 font-semibold">{{ 'tickets.sla.title' | translate }}</h2>
        @if (cycles(); as list) {
          <ol class="space-y-2 text-sm">
            @for (c of list; track c.id) {
              <li class="rounded-lg bg-surface p-2">
                <p class="font-medium">{{ 'tickets.sla.cycle' | translate: { n: c.cycleNumber } }} · {{ 'tickets.sla.reason.' + pascal(c.reason) | translate }}</p>
                <p class="text-muted">{{ 'tickets.sla.started' | translate }}: {{ c.startedAtUtc | date: 'short' }}</p>
                <p class="text-muted">{{ 'tickets.sla.responseDue' | translate }}: {{ c.responseDueAtUtc ? (c.responseDueAtUtc | date: 'short') : ('common.na' | translate) }}
                  · {{ 'tickets.sla.firstResponse' | translate }}: {{ c.firstResponseAtUtc ? (c.firstResponseAtUtc | date: 'short') : ('common.na' | translate) }}</p>
                <p class="text-muted">{{ 'tickets.sla.resolutionDue' | translate }}: {{ c.resolutionDueAtUtc | date: 'short' }}
                  · {{ 'tickets.sla.resolvedAt' | translate }}: {{ c.resolvedAtUtc ? (c.resolvedAtUtc | date: 'short') : ('common.na' | translate) }}</p>
                <p class="font-medium" [class]="c.resolutionState === 'MISSED' || c.resolutionState === 'OVERDUE' ? 'text-danger' : 'text-success'">{{ 'tickets.sla.state.' + pascal(c.resolutionState) | translate }}</p>
              </li>
            }
          </ol>
        } @else { <p class="text-sm text-muted">{{ 'common.loading' | translate }}</p> }
      </section>
    }
  `,
})
export class TicketActions {
  readonly ticket = input.required<TicketDetailDto>();
  readonly changed = output<void>();

  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly gql = inject(GraphqlClient);

  protected readonly pascal = pascal;
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly members = signal<MemberDto[]>([]);
  protected readonly cycles = signal<TicketSlaHistoryQuery['ticketSla']['cycles'] | null>(null);

  protected readonly isManager = computed(() => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager');
  protected readonly next = computed(() => NEXT[this.ticket().summary.status]);
  protected readonly canReopen = computed(() => ['Resolved', 'Closed'].includes(this.ticket().summary.status));
  protected readonly assignable = computed(() => {
    const taken = new Set(this.ticket().assignees?.map((a) => a.membershipId));
    return this.members().filter((m) => m.role === 'Agent' && m.isActive && !taken.has(m.id));
  });

  constructor() {
    effect(() => {
      if (this.isManager()) untracked(() => this.api.members().subscribe((m) => this.members.set(m)));
    });
    effect(() => {
      const id = this.ticket().summary.id;
      this.ticket().summary.version; // re-read the cycle history after every change (a reopen adds a cycle)
      if (this.tenant.isStaff()) untracked(() => this.gql.query(TicketSlaHistoryDocument, { ticketId: id }).subscribe({ next: (r) => this.cycles.set(r.ticketSla.cycles), error: () => this.cycles.set([]) }));
    });
  }

  protected move(status: TicketStatus): void {
    this.run(this.api.changeStatus(this.ticket().summary.id, status, Number(this.ticket().summary.version)));
  }

  protected reopen(): void {
    this.run(this.api.reopen(this.ticket().summary.id, Number(this.ticket().summary.version)));
  }

  protected assign(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const id = select.value;
    select.value = '';
    if (id) this.run(this.api.assign(this.ticket().summary.id, [id], Number(this.ticket().summary.version)));
  }

  protected unassign(membershipId: string): void {
    this.run(this.api.unassign(this.ticket().summary.id, membershipId, Number(this.ticket().summary.version)));
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
        this.message.set(status === 409 ? 'tickets.errors.conflict' : status === 403 ? 'common.forbidden' : status === 400 ? 'tickets.errors.invalid' : 'tickets.errors.generic');
        this.busy.set(false);
        this.changed.emit(); // reload the authoritative state (never retry automatically)
      },
    });
  }
}
