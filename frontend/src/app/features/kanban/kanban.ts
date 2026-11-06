import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, filter, merge } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import {
  BoardTicketFragment,
  KanbanBoardDocument,
  KanbanBoardQuery,
  TeamMembersDocument,
  TeamMembersQuery,
  TicketTaskProgressDocument,
} from '../../core/graphql/generated';
import { NotificationCreatedEvent } from '../../core/notifications/notification.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { activeWorkspaceEvents } from '../../core/realtime/workspace-events';
import { pascal, toRestStatus } from '../../shared/format';
import { TicketsApi } from '../tickets/tickets-api';
import { AgentPicker, PickerAgent } from './agent-picker/agent-picker';
import { CardTasks } from './card-tasks/card-tasks';

type Column = KanbanBoardQuery['kanbanBoard'][number];
type MemberRow = TeamMembersQuery['teamMembers']['items'][number];

// Mirrors the backend TicketWorkflow ONLY to hide impossible drop targets; the server validates every move. Resolved and Closed
// tickets go back only through Reopen, which starts a new SLA cycle.
const TARGETS: Record<string, string[]> = {
  ASSIGNED: ['IN_PROGRESS', 'RESOLVED'],
  IN_PROGRESS: ['ON_HOLD', 'RESOLVED'],
  ON_HOLD: ['IN_PROGRESS', 'RESOLVED'],
  RESOLVED: ['CLOSED'],
};

@Component({
  selector: 'app-kanban',
  imports: [CdkDropListGroup, CdkDropList, CdkDrag, CdkDragHandle, AgentPicker, CardTasks, ReactiveFormsModule, TranslatePipe, DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-end justify-between gap-4">
      <h1 class="text-2xl font-bold tracking-tight">{{ 'kanban.title' | translate }}</h1>
      <form [formGroup]="form" class="flex flex-wrap items-end gap-3 text-sm" (ngSubmit)="load()">
        <label class="grid gap-1"
          ><span class="text-muted">{{ 'filters.search' | translate }}</span>
          <input
            type="search"
            formControlName="search"
            class="rounded-lg border border-border bg-card px-2 py-1"
        /></label>
        <label class="grid gap-1"
          ><span class="text-muted">{{ 'filters.priority' | translate }}</span>
          <select
            formControlName="priority"
            class="rounded-lg border border-border bg-card px-2 py-1"
          >
            <option value="">{{ 'filters.any' | translate }}</option>
            @for (p of priorities; track p) {
              <option [value]="p">{{ 'tickets.priority.' + p | translate }}</option>
            }
          </select></label
        >
        <label class="flex items-center gap-2 py-1.5"
          ><input type="checkbox" formControlName="overdue" />
          {{ 'kanban.overdueOnly' | translate }}</label
        >
        <label class="flex items-center gap-2 py-1.5"
          ><input type="checkbox" formControlName="mine" />
          {{ 'kanban.mineOnly' | translate }}</label
        >
        <button
          type="submit"
          class="rounded-lg bg-brand px-3 py-1.5 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
        >
          {{ 'filters.apply' | translate }}
        </button>
      </form>
    </div>

    @if (message(); as m) {
      @if (m.error) {
        <p
          role="alert"
          class="mt-4 rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger"
        >
          {{ m.key | translate }}
        </p>
      } @else {
        <p role="status" class="mt-4 rounded-lg border border-success/40 bg-success/10 px-3 py-2 text-sm">
          {{ m.key | translate }}
        </p>
      }
    }

    <div class="mt-6" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">
          {{ 'kanban.loadError' | translate }}
          <button type="button" class="ml-2 underline" (click)="load()">
            {{ 'common.retry' | translate }}
          </button>
        </p>
      } @else if (!board()) {
        <p class="text-muted">{{ 'common.loading' | translate }}</p>
      } @else {
        <div class="flex gap-4 overflow-x-auto pb-4" cdkDropListGroup>
          @for (col of board(); track col.status) {
            <section
              class="w-72 shrink-0 rounded-xl border border-border bg-surface"
              [attr.aria-label]="'tickets.status.' + pascal(col.status) | translate"
            >
              <header class="flex items-center justify-between px-3 py-2">
                <h2 class="text-sm font-semibold">
                  {{ 'tickets.status.' + pascal(col.status) | translate }}
                </h2>
                <span class="rounded-full bg-card px-2 text-xs tabular-nums">{{ col.total }}</span>
              </header>
              <div
                cdkDropList
                cdkDropListSortingDisabled
                [id]="col.status"
                [cdkDropListData]="col"
                [cdkDropListEnterPredicate]="canEnter(col.status)"
                (cdkDropListDropped)="drop($event)"
                class="min-h-24 space-y-2 px-2 pb-2"
              >
                @for (t of col.tickets; track t.id) {
                  <article
                    cdkDrag
                    [cdkDragData]="t"
                    [cdkDragDisabled]="pending().has(t.id) || !canMove(t)"
                    [cdkDragStartDelay]="{ touch: 250, mouse: 0 }"
                    class="rounded-lg border bg-card p-3 text-sm shadow-sm"
                    [class]="t.isOverdue ? 'border-danger' : 'border-border'"
                    [class.opacity-60]="pending().has(t.id)"
                  >
                    <div cdkDragHandle [cdkDragHandleDisabled]="!canMove(t)" [class.cursor-grab]="canMove(t)">
                    <a
                      [routerLink]="['/tickets', t.id]"
                      class="font-medium hover:underline focus-visible:outline-2 focus-visible:outline-brand"
                    >
                      <span class="font-mono text-xs text-muted">{{ t.displayNumber }}</span>
                      {{ t.title }}</a
                    >
                    <p class="mt-2 flex flex-wrap gap-2 text-xs text-muted">
                      <span>{{ 'tickets.priority.' + pascal(t.priority) | translate }}</span>
                      @if (t.categoryName) {
                        <span>· {{ t.categoryName }}</span>
                      }
                      @if (t.assigneeCount !== null) {
                        <span>· {{ 'kanban.assignees' | translate: { n: t.assigneeCount } }}</span>
                      }
                      @if (tenant.role() === 'Agent' && t.assignedToMe === false) {
                        <span>· {{ 'kanban.notAssignedToYou' | translate }}</span>
                      }
                    </p>
                    </div>
                    @if (tenant.isStaff()) {
                      <div class="mt-2">
                        <app-agent-picker
                          [ticketKey]="t.id"
                          [assignees]="t.assignees ?? []"
                          [agents]="agents()"
                          [loading]="membersLoading()"
                          [loadFailed]="membersFailed()"
                          [me]="meMembership()"
                          [busyId]="assigning()?.ticketId === t.id ? assigning()!.membershipId : null"
                          [readOnly]="t.status === 'RESOLVED' || t.status === 'CLOSED'"
                          (pick)="assign(t, $event)"
                          (retry)="loadMembers()"
                        />
                      </div>
                      <div class="mt-1">
                        <app-card-tasks
                          [ticketId]="t.id"
                          [progress]="progress().get(t.id) ?? null"
                          [canWork]="manager() || t.assignedToMe === true"
                          [isManager]="manager()"
                          [canClaim]="!manager() && t.assignedToMe === false && !!meMembership()"
                          [claiming]="assigning()?.ticketId === t.id && assigning()!.membershipId === meMembership()"
                          [finished]="t.status === 'RESOLVED' || t.status === 'CLOSED'"
                          [assignees]="t.assignees ?? []"
                          (claim)="claim(t)"
                          (changed)="loadProgress()"
                        />
                      </div>
                    }
                    @if (t.resolutionDueAtUtc) {
                      <p
                        class="mt-1 text-xs"
                        [class]="t.isOverdue ? 'font-semibold text-danger' : 'text-muted'"
                      >
                        {{ (t.isOverdue ? 'kanban.overdue' : 'kanban.due') | translate }}
                        <time [attr.datetime]="t.resolutionDueAtUtc">{{
                          t.resolutionDueAtUtc | date: 'short'
                        }}</time>
                      </p>
                    }
                    @if (targets(t).length) {
                      <label class="mt-2 grid gap-1 text-xs"
                        ><span class="text-muted">{{ 'kanban.moveTo' | translate }}</span>
                        <select
                          class="rounded-md border border-border bg-card px-1 py-1"
                          [disabled]="pending().has(t.id)"
                          (change)="moveTo(t, $event)"
                        >
                          <option value="">—</option>
                          @for (s of targets(t); track s) {
                            <option [value]="s">
                              {{ 'tickets.status.' + pascal(s) | translate }}
                            </option>
                          }
                        </select></label
                      >
                    }
                    @if (canReopen(t)) {
                      <button
                        type="button"
                        class="mt-2 rounded-md border border-border px-2 py-1 text-xs font-semibold hover:bg-surface focus-visible:outline-2 focus-visible:outline-brand disabled:opacity-60"
                        [disabled]="pending().has(t.id)"
                        [attr.aria-busy]="pending().has(t.id)"
                        (click)="reopen(t)"
                      >
                        {{ (pending().has(t.id) ? 'kanban.reopening' : 'tickets.actions.reopen') | translate }}
                      </button>
                    }
                  </article>
                } @empty {
                  <p class="px-1 py-4 text-center text-xs text-muted">
                    {{ 'kanban.empty' | translate }}
                  </p>
                }
              </div>
              @if (col.tickets.length < col.total) {
                <p class="px-3 pb-2 text-xs text-muted">
                  {{ 'kanban.more' | translate: { n: col.total - col.tickets.length } }}
                </p>
              }
            </section>
          }
        </div>
      }
    </div>
  `,
})
export class Kanban {
  private readonly gql = inject(GraphqlClient);
  private readonly rest = inject(TicketsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly tenant = inject(TenantService);
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(AuthService);

  protected readonly pascal = pascal;
  protected readonly priorities = ['Low', 'Medium', 'High', 'Urgent'];
  protected readonly board = signal<Column[] | null>(null);
  protected readonly error = signal(false);
  protected readonly message = signal<{ key: string; error: boolean } | null>(null);
  protected readonly manager = computed(
    () => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager',
  );
  protected readonly pending = signal<ReadonlySet<string>>(new Set());
  /** The assignment request in flight (one at a time); only that picker option is disabled. */
  protected readonly assigning = signal<{ ticketId: string; membershipId: string } | null>(null);
  /** Staff of the CURRENT workspace, read once for every card's picker (never per card). Cleared on a workspace switch. */
  private readonly members = signal<MemberRow[]>([]);
  protected readonly membersLoading = signal(false);
  protected readonly membersFailed = signal(false);
  protected readonly agents = computed<PickerAgent[]>(() =>
    this.members()
      .filter((m) => m.role === 'AGENT' && m.isActive)
      .map((m) => ({ id: m.id, userId: m.userId, displayName: m.displayName, email: m.email })),
  );
  protected readonly meMembership = computed(
    () => this.members().find((m) => m.userId === this.auth.user()?.id)?.id ?? null,
  );
  private membersSeq = 0;
  protected readonly progress = signal<
    ReadonlyMap<string, { totalTasks: number; completedTasks: number }>
  >(new Map());
  protected readonly form = new FormGroup({
    search: new FormControl('', { nonNullable: true }),
    priority: new FormControl('', { nonNullable: true }),
    overdue: new FormControl(this.route.snapshot.queryParamMap.has('overdue'), {
      nonNullable: true,
    }),
    mine: new FormControl(false, { nonNullable: true }),
  });
  private seq = 0;

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) {
        untracked(() => {
          this.load();
          this.loadMembers();
        });
      } else {
        this.board.set(null);
        this.members.set([]);
      }
    });
    merge(
      this.realtime
        .on<NotificationCreatedEvent>('notification.created')
        .pipe(filter((e) => e.notification.tenantId === this.tenant.tenantId())),
      activeWorkspaceEvents('ticket.changed'),
      this.realtime.reconnected,
    )
      .pipe(debounceTime(1000), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
    // A task change only affects the progress indicators, so the board itself is not reloaded.
    this.realtime
      .on('ticket.task.changed')
      .pipe(debounceTime(1000), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.loadProgress());
  }

  protected load(): void {
    const { search, priority, overdue, mine } = this.form.getRawValue();
    const id = ++this.seq;
    this.error.set(false);
    this.gql
      .query(KanbanBoardDocument, {
        perColumn: 20,
        filter: {
          search: search || null,
          priority: priority ? (priority.toUpperCase() as 'LOW') : null,
          overdue: overdue || null,
          assignedToMe: mine,
        },
      })
      .subscribe({
        next: (r) => {
          if (id === this.seq && this.pending().size === 0) {
            this.board.set(r.kanbanBoard); // never overwrite a move in flight
            this.loadProgress();
          }
        },
        error: () => this.error.set(true),
      });
  }

  /** ONE internal query for the whole board instead of a request per card; never issued for customers. */
  protected loadProgress(): void {
    const ids = (this.board() ?? []).flatMap((c) => c.tickets.map((t) => t.id));
    if (!this.tenant.isStaff() || !ids.length) return this.progress.set(new Map());
    this.gql.query(TicketTaskProgressDocument, { ticketIds: ids }).subscribe({
      next: (r) => this.progress.set(new Map(r.ticketTaskProgress.map((p) => [p.ticketId, p]))),
      error: () => this.progress.set(new Map()), // optional indicator: the board itself stays usable
    });
  }

  /**
   * Role-aware view of TicketWorkflowService: Owners and Managers get every workflow target; an Agent only on a ticket assigned to them
   * (server-computed `assignedToMe`) and never Closed; Customers get none. The backend still authorizes every move.
   */
  protected targets(t: BoardTicketFragment): string[] {
    const all = TARGETS[t.status] ?? [];
    if (this.manager()) return all;
    if (!this.tenant.isStaff() || !t.assignedToMe) return [];
    return all.filter((s) => s !== 'CLOSED');
  }

  protected loadMembers(): void {
    this.members.set([]); // options always belong to the current workspace
    if (!this.tenant.isStaff()) return;
    const tenantId = this.tenant.tenantId();
    const seq = ++this.membersSeq;
    this.membersLoading.set(true);
    this.membersFailed.set(false);
    this.gql.query(TeamMembersDocument, { page: 1, pageSize: 100 }).subscribe({
      next: (r) => {
        if (seq !== this.membersSeq || tenantId !== this.tenant.tenantId()) return; // a delayed answer of another workspace
        this.members.set(r.teamMembers.items);
        this.membersLoading.set(false);
      },
      error: () => {
        if (seq !== this.membersSeq) return;
        this.membersLoading.set(false);
        this.membersFailed.set(true);
      },
    });
  }

  /** Adds an assignment (self-claim or a teammate). Shown only after the server confirmed: the board is reloaded either way. */
  protected assign(t: BoardTicketFragment, membershipId: string): void {
    if (this.assigning()) return;
    this.assigning.set({ ticketId: t.id, membershipId });
    this.message.set(null);
    this.rest.assign(t.id, [membershipId], t.version).subscribe({
      next: () => {
        this.assigning.set(null);
        this.load();
      },
      error: (e: unknown) => {
        this.assigning.set(null);
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        this.message.set({
          key:
            status === 409
              ? 'kanban.assign.errors.conflict'
              : status === 403
                ? 'kanban.assign.errors.forbidden'
                : status === 400
                  ? 'kanban.assign.errors.invalid'
                  : 'kanban.assign.errors.generic',
          error: true,
        });
        this.load(); // authoritative state again
      },
    });
  }

  protected claim(t: BoardTicketFragment): void {
    const me = this.meMembership();
    if (me) this.assign(t, me);
  }

  protected canReopen(t: BoardTicketFragment): boolean {
    if (t.status !== 'RESOLVED' && t.status !== 'CLOSED') return false;
    return this.manager() || (this.tenant.isStaff() && !!t.assignedToMe);
  }

  protected canMove(t: BoardTicketFragment): boolean {
    return this.targets(t).length > 0;
  }

  protected canEnter(status: string) {
    return (drag: CdkDrag<BoardTicketFragment>) => this.targets(drag.data).includes(status);
  }

  protected drop(event: CdkDragDrop<Column, Column, BoardTicketFragment>): void {
    if (event.previousContainer === event.container) return;
    this.move(event.item.data, event.container.data.status);
  }

  protected moveTo(t: BoardTicketFragment, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const target = select.value;
    select.value = '';
    if (target) this.move(t, target);
  }

  /** The REST command decides; the board is reconciled from the server afterwards (success AND failure), never patched optimistically. */
  private move(t: BoardTicketFragment, target: string): void {
    if (this.pending().has(t.id) || !this.targets(t).includes(target)) return;
    this.setPending(t.id, true);
    this.message.set(null);
    this.rest.changeStatus(t.id, toRestStatus(target), t.version).subscribe({
      next: () => this.settle(t.id),
      error: (e: unknown) => this.fail(t.id, e, 'kanban.errors.generic'),
    });
  }

  /** Reopen is its own command (new SLA cycle); the card lands where the server puts it (Assigned or Open). */
  protected reopen(t: BoardTicketFragment): void {
    if (this.pending().has(t.id) || !this.canReopen(t)) return;
    this.setPending(t.id, true);
    this.message.set(null);
    this.rest.reopen(t.id, t.version).subscribe({
      next: () => {
        this.message.set({ key: 'kanban.reopened', error: false });
        this.settle(t.id);
      },
      error: (e: unknown) => this.fail(t.id, e, 'kanban.errors.reopen'),
    });
  }

  private fail(id: string, e: unknown, fallback: string): void {
    const status = e instanceof HttpErrorResponse ? e.status : 0;
    const key =
      status === 409
        ? 'kanban.errors.conflict'
        : status === 403
          ? 'kanban.errors.forbidden'
          : status === 400
            ? 'kanban.errors.invalid'
            : fallback;
    this.message.set({ key, error: true });
    this.settle(id); // reload from the server: the card returns to its real column
  }

  private settle(id: string): void {
    this.setPending(id, false);
    this.load();
  }

  private setPending(id: string, on: boolean): void {
    this.pending.update((s) => {
      const next = new Set(s);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}
