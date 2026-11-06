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
  templateUrl: './kanban.html',
  styleUrl: './kanban.css',
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
