import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, filter } from 'rxjs';
import { TicketTaskDto } from '../../../core/api/generated/models';
import { RealtimeService } from '../../../core/realtime/realtime.service';
import { initials } from '../../workspaces/workspace-api';
import { TicketsApi } from '../../tickets/tickets-api';
import { PickerAssignee } from '../agent-picker/agent-picker';

const TITLE_MAX = 200;

/**
 * Child tasks inside a board card: a completion summary that expands into the task rows, completion and reopening of single tasks, and
 * an inline "Add task" editor. Rows are read lazily when expanded (never per card on load). The server decides every write; an Agent
 * must be assigned to the parent ticket first, so the editor offers an explicit Claim step and keeps the typed title meanwhile.
 */
@Component({
  selector: 'app-card-tasks',
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './card-tasks.html',
  styleUrl: './card-tasks.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CardTasks {
  private readonly api = inject(TicketsApi);
  private readonly realtime = inject(RealtimeService);

  readonly ticketId = input.required<string>();
  /** Counts of the whole board come from ONE grouped query; absent for tickets whose tasks the caller may not see. */
  readonly progress = input<{ totalTasks: number; completedTasks: number } | null>(null);
  /** Owners/Managers, or an Agent assigned to the parent ticket: the callers the server lets write tasks. */
  readonly canWork = input(false);
  readonly isManager = input(false);
  /** An Agent who is not assigned yet and may claim the ticket first. */
  readonly canClaim = input(false);
  readonly claiming = input(false);
  /** Resolved and Closed tickets keep their tasks read-only. */
  readonly finished = input(false);
  readonly assignees = input<readonly PickerAssignee[]>([]);

  readonly claim = output<void>();
  /** Something changed on the server; the board refreshes its counts. */
  readonly changed = output<void>();

  protected readonly initials = initials;
  protected readonly titleMax = TITLE_MAX;
  protected readonly expanded = signal(false);
  protected readonly adding = signal(false);
  protected readonly tasks = signal<TicketTaskDto[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly toggling = signal<ReadonlySet<string>>(new Set());
  protected readonly error = signal<string | null>(null);
  protected readonly title = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(TITLE_MAX)],
  });
  protected readonly assignee = new FormControl('', { nonNullable: true });
  private readonly titleField = viewChild<ElementRef<HTMLInputElement>>('titleInput');

  protected readonly total = computed(() => this.progress()?.totalTasks ?? 0);
  protected readonly done = computed(() => this.progress()?.completedTasks ?? 0);
  protected readonly percent = computed(() => (this.total() ? (this.done() / this.total()) * 100 : 0));
  protected readonly showAdd = computed(() => !this.finished() && (this.canWork() || this.canClaim()));

  constructor() {
    this.realtime
      .on<{ ticketId: string }>('ticket.task.changed')
      .pipe(
        filter((e) => e.ticketId === this.ticketId()),
        debounceTime(500),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe(() => {
        if (this.expanded()) this.load();
      });
  }

  protected toggle(): void {
    this.expanded.update((v) => !v);
    if (this.expanded() && this.tasks() === null) this.load();
  }

  protected startAdding(): void {
    this.adding.set(true);
    this.error.set(null);
    if (this.tasks() === null && this.total() > 0) this.load();
    this.expanded.set(true);
    setTimeout(() => this.titleField()?.nativeElement.focus());
  }

  protected cancel(): void {
    this.adding.set(false);
    this.title.reset('');
    this.error.set(null);
  }

  protected submit(): void {
    const title = this.title.value.trim();
    if (!this.canWork() || !title || title.length > TITLE_MAX || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    // An Agent's task is always theirs (server-derived); Owners and Managers name one of the ticket's assignees.
    const assignee = this.isManager() ? this.assignee.value || this.assignees()[0]?.membershipId || null : null;
    this.api.createTask(this.ticketId(), title, null, assignee).subscribe({
      next: () => {
        this.busy.set(false);
        this.title.reset('');
        this.load();
        this.changed.emit();
        setTimeout(() => this.titleField()?.nativeElement.focus()); // stay open for rapid entry
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(e)); // the typed title is kept for a retry
      },
    });
  }

  protected setDone(task: TicketTaskDto, done: boolean): void {
    if (this.toggling().has(task.id)) return;
    this.setToggling(task.id, true);
    this.error.set(null);
    this.api.changeTaskStatus(this.ticketId(), task.id, done ? 'Done' : 'Todo', Number(task.version)).subscribe({
      next: () => {
        this.setToggling(task.id, false);
        this.load();
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.setToggling(task.id, false);
        this.error.set(errorKey(e));
        this.load(); // show the authoritative state again
        this.changed.emit();
      },
    });
  }

  protected onKey(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancel();
    }
  }

  private load(): void {
    this.loading.set(true);
    this.api.tasks(this.ticketId()).subscribe({
      next: (list) => {
        this.tasks.set(list.tasks);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.error.set(errorKey(e));
      },
    });
  }

  private setToggling(id: string, on: boolean): void {
    this.toggling.update((s) => {
      const next = new Set(s);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}

function errorKey(e: unknown): string {
  const status = e instanceof HttpErrorResponse ? e.status : 0;
  return status === 409
    ? 'kanban.tasks.errors.conflict'
    : status === 403
      ? 'kanban.tasks.errors.forbidden'
      : status === 400
        ? 'kanban.tasks.errors.invalid'
        : 'kanban.tasks.errors.generic';
}
