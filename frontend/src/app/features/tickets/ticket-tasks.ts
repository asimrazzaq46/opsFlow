import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, moveItemInArray } from '@angular/cdk/drag-drop';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, debounceTime, filter } from 'rxjs';
import { TicketDetailDto, TicketTaskDto, TicketTaskListDto, TicketTaskStatus, TicketTaskSummaryDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { TicketsApi } from './tickets-api';

interface TaskEvent {
  tenantId: string;
  ticketId: string;
  taskId: string | null;
  change: string;
  version: number | null;
}

export const TASK_STATUSES: TicketTaskStatus[] = ['Todo', 'InProgress', 'Done'];

export function summarize(tasks: TicketTaskDto[]): TicketTaskSummaryDto {
  const count = (s: TicketTaskStatus) => tasks.filter((t) => t.status === s).length;
  return { totalTasks: tasks.length, todoTasks: count('Todo'), inProgressTasks: count('InProgress'), completedTasks: count('Done') };
}

export function taskPercent(s: TicketTaskSummaryDto): number {
  const total = Number(s.totalTasks);
  return total ? Math.round((Number(s.completedTasks) / total) * 100) : 0;
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '?') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase();
}

function errorKey(e: unknown): string {
  const status = e instanceof HttpErrorResponse ? e.status : 0;
  return status === 409 ? 'tasks.errors.conflict' : status === 403 ? 'tasks.errors.forbidden' : status === 404 ? 'tasks.errors.notFound' : status === 400 ? 'tasks.errors.invalid' : 'tasks.errors.generic';
}

const BADGE: Record<TicketTaskStatus, string> = {
  Todo: 'bg-surface text-muted border-border',
  InProgress: 'bg-brand/10 text-brand border-brand/40',
  Done: 'bg-success/10 text-success border-success/40',
};

/** Internal child tasks of a ticket. Staff only: the parent renders it only for non-customers; the API rejects customers regardless. */
@Component({
  selector: 'app-ticket-tasks',
  imports: [ReactiveFormsModule, TranslatePipe, DatePipe, CdkDropList, CdkDrag, CdkDragHandle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ticket-tasks.html',
})
export class TicketTasks {
  readonly ticket = input.required<TicketDetailDto>();

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);
  private readonly realtime = inject(RealtimeService);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  private readonly section = viewChild<ElementRef<HTMLElement>>('section');

  protected readonly statuses = TASK_STATUSES;
  protected readonly badge = BADGE;
  protected readonly initials = initials;
  protected readonly list = signal<TicketTaskListDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly creating = signal(false);
  protected readonly editingId = signal<string | null>(null);

  protected readonly isManager = computed(() => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager');
  protected readonly percent = computed(() => taskPercent(this.list()?.summary ?? summarize([])));
  /** Only ACTIVE agents currently assigned to the ticket can own tasks (the server re-validates). */
  protected readonly eligibleAssignees = computed(() => (this.ticket().assignees ?? []).filter((a) => a.isAgentActive));
  private readonly ticketId = computed(() => this.ticket().summary.id);

  protected readonly createForm = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200), Validators.pattern(/\S/)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(4000)] }),
    assignee: new FormControl('', { nonNullable: true }),
  });
  protected readonly editForm = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200), Validators.pattern(/\S/)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(4000)] }),
  });
  private seq = 0;
  private shownId: string | null = null;
  private scrolled = false;

  constructor() {
    const destroyRef = inject(DestroyRef);
    effect(() => {
      const id = this.ticketId();
      this.ticket().summary.version; // a status/assignment change can make tasks read-only or change eligibility
      untracked(() => {
        if (id !== this.shownId) {
          this.shownId = id;
          this.list.set(null);
          this.creating.set(false);
          this.editingId.set(null);
        }
        this.load();
      });
    });
    this.realtime
      .on<TaskEvent>('ticket.task.changed')
      .pipe(
        filter((e) => e.tenantId === this.tenant.tenantId() && e.ticketId === this.ticketId() && !this.isKnown(e)),
        debounceTime(250),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe(() => this.load());
    this.realtime.reconnected.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.load());
  }

  load(): void {
    const id = ++this.seq;
    this.loading.set(true);
    this.error.set(false);
    this.api.tasks(this.ticketId()).subscribe({
      next: (l) => {
        if (id !== this.seq) return;
        this.list.set(l);
        this.loading.set(false);
        this.scrollIfRequested();
      },
      error: () => {
        if (id !== this.seq) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected openCreate(): void {
    this.createForm.reset();
    this.createForm.controls.assignee.setValidators(this.isManager() ? [Validators.required] : []);
    this.createForm.controls.assignee.updateValueAndValidity();
    this.message.set(null);
    this.creating.set(true);
  }

  protected create(): void {
    if (this.createForm.invalid) {
      this.createForm.markAllAsTouched();
      return;
    }
    const { title, description, assignee } = this.createForm.getRawValue();
    this.run(this.api.createTask(this.ticketId(), title.trim(), description.trim() || null, this.isManager() ? assignee : null), (t) => {
      this.creating.set(false);
      this.apply(t);
    });
  }

  protected startEdit(t: TicketTaskDto): void {
    this.editForm.setValue({ title: t.title, description: t.description ?? '' });
    this.message.set(null);
    this.editingId.set(t.id);
  }

  protected saveEdit(t: TicketTaskDto): void {
    if (this.editForm.invalid) return;
    const { title, description } = this.editForm.getRawValue();
    this.run(this.api.updateTask(this.ticketId(), t.id, title.trim(), description.trim() || null, Number(t.version)), (updated) => {
      this.editingId.set(null);
      this.apply(updated);
    });
  }

  protected setStatus(t: TicketTaskDto, status: TicketTaskStatus): void {
    if (t.status !== status) this.run(this.api.changeTaskStatus(this.ticketId(), t.id, status, Number(t.version)), (u) => this.apply(u));
  }

  protected reassign(t: TicketTaskDto, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const id = select.value;
    select.value = '';
    if (id) this.run(this.api.reassignTask(this.ticketId(), t.id, id, Number(t.version)), (u) => this.apply(u));
  }

  protected drop(event: CdkDragDrop<unknown, unknown, TicketTaskDto>): void {
    if (event.previousIndex !== event.currentIndex) this.reorder(event.previousIndex, event.currentIndex);
  }

  /** Keyboard alternative to drag and drop. */
  protected move(index: number, delta: number): void {
    this.reorder(index, index + delta);
  }

  private reorder(from: number, to: number): void {
    const l = this.list();
    if (!l || this.busy() || to < 0 || to >= l.tasks.length) return;
    const tasks = [...l.tasks];
    moveItemInArray(tasks, from, to);
    this.list.set({ ...l, tasks }); // safe optimistic order: the server confirms it or the list is reloaded
    this.run(this.api.reorderTasks(this.ticketId(), tasks.map((t) => ({ taskId: t.id, version: Number(t.version) }))), (r) => this.list.set(r));
  }

  /** The server decides; every failure shows a message and reloads the authoritative list (never retries automatically). */
  private run<T>(call: Observable<T>, done: (result: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    call.subscribe({
      next: (r) => {
        this.busy.set(false);
        done(r);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.message.set(errorKey(e));
        if (e instanceof HttpErrorResponse && e.status === 409) this.editingId.set(null);
        this.load();
      },
    });
  }

  private apply(task: TicketTaskDto): void {
    const l = this.list();
    if (!l) return this.load();
    const tasks = l.tasks.some((t) => t.id === task.id) ? l.tasks.map((t) => (t.id === task.id ? task : t)) : [...l.tasks, task];
    this.list.set({ ...l, tasks, summary: summarize(tasks) });
  }

  /** Skips the echo of our own change (we already hold that version). */
  private isKnown(e: TaskEvent): boolean {
    const t = e.taskId ? this.list()?.tasks.find((x) => x.id === e.taskId) : undefined;
    return !!t && e.version !== null && Number(t.version) >= e.version;
  }

  private scrollIfRequested(): void {
    if (this.scrolled || this.route.snapshot.fragment !== 'child-tasks') return;
    this.scrolled = true;
    afterNextRender(() => {
      const el = this.section()?.nativeElement;
      el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      el?.focus({ preventScroll: true });
    }, { injector: this.injector });
  }
}
