import { CdkDrag, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, merge } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { BoardTicketFragment, KanbanBoardDocument, KanbanBoardQuery } from '../../core/graphql/generated';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { pascal, toRestStatus } from '../../shared/format';
import { TicketsApi } from '../tickets/tickets-api';

type Column = KanbanBoardQuery['kanbanBoard'][number];

// Mirrors the backend TicketWorkflow ONLY to hide impossible drop targets; the server validates every move.
const TARGETS: Record<string, string[]> = {
  ASSIGNED: ['IN_PROGRESS', 'RESOLVED'],
  IN_PROGRESS: ['ON_HOLD', 'RESOLVED'],
  ON_HOLD: ['IN_PROGRESS', 'RESOLVED'],
  RESOLVED: ['CLOSED'],
};

@Component({
  selector: 'app-kanban',
  imports: [CdkDropList, CdkDrag, ReactiveFormsModule, TranslatePipe, DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-end justify-between gap-4">
      <h1 class="text-2xl font-bold tracking-tight">{{ 'kanban.title' | translate }}</h1>
      <form [formGroup]="form" class="flex flex-wrap items-end gap-3 text-sm" (ngSubmit)="load()">
        <label class="grid gap-1"><span class="text-muted">{{ 'filters.search' | translate }}</span>
          <input type="search" formControlName="search" class="rounded-lg border border-border bg-card px-2 py-1" /></label>
        <label class="grid gap-1"><span class="text-muted">{{ 'filters.priority' | translate }}</span>
          <select formControlName="priority" class="rounded-lg border border-border bg-card px-2 py-1">
            <option value="">{{ 'filters.any' | translate }}</option>
            @for (p of priorities; track p) { <option [value]="p">{{ 'tickets.priority.' + p | translate }}</option> }
          </select></label>
        <label class="flex items-center gap-2 py-1.5"><input type="checkbox" formControlName="overdue" /> {{ 'kanban.overdueOnly' | translate }}</label>
        <button type="submit" class="rounded-lg bg-brand px-3 py-1.5 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">{{ 'filters.apply' | translate }}</button>
      </form>
    </div>

    @if (message(); as m) {
      <p role="alert" class="mt-4 rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ m | translate }}</p>
    }

    <div class="mt-6" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">{{ 'kanban.loadError' | translate }}
          <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (!board()) {
        <p class="text-muted">{{ 'common.loading' | translate }}</p>
      } @else {
        <div class="flex gap-4 overflow-x-auto pb-4" cdkDropListGroup>
          @for (col of board(); track col.status) {
            <section class="w-72 shrink-0 rounded-xl border border-border bg-surface" [attr.aria-label]="'tickets.status.' + pascal(col.status) | translate">
              <header class="flex items-center justify-between px-3 py-2">
                <h2 class="text-sm font-semibold">{{ 'tickets.status.' + pascal(col.status) | translate }}</h2>
                <span class="rounded-full bg-card px-2 text-xs tabular-nums">{{ col.total }}</span>
              </header>
              <div cdkDropList [id]="col.status" [cdkDropListData]="col" [cdkDropListEnterPredicate]="canEnter(col.status)" (cdkDropListDropped)="drop($event)"
                class="min-h-24 space-y-2 px-2 pb-2">
                @for (t of col.tickets; track t.id) {
                  <article cdkDrag [cdkDragData]="t" [cdkDragDisabled]="pending().has(t.id) || !canMove(t)"
                    class="rounded-lg border bg-card p-3 text-sm shadow-sm" [class]="t.isOverdue ? 'border-danger' : 'border-border'" [class.opacity-60]="pending().has(t.id)">
                    <a [routerLink]="['/tickets', t.id]" class="font-medium hover:underline focus-visible:outline-2 focus-visible:outline-brand">
                      <span class="font-mono text-xs text-muted">{{ t.displayNumber }}</span> {{ t.title }}</a>
                    <p class="mt-2 flex flex-wrap gap-2 text-xs text-muted">
                      <span>{{ 'tickets.priority.' + pascal(t.priority) | translate }}</span>
                      @if (t.categoryName) { <span>· {{ t.categoryName }}</span> }
                      @if (t.assigneeCount !== null) { <span>· {{ 'kanban.assignees' | translate: { n: t.assigneeCount } }}</span> }
                    </p>
                    @if (t.resolutionDueAtUtc) {
                      <p class="mt-1 text-xs" [class]="t.isOverdue ? 'font-semibold text-danger' : 'text-muted'">
                        {{ (t.isOverdue ? 'kanban.overdue' : 'kanban.due') | translate }} <time [attr.datetime]="t.resolutionDueAtUtc">{{ t.resolutionDueAtUtc | date: 'short' }}</time></p>
                    }
                    @if (targets(t).length) {
                      <label class="mt-2 grid gap-1 text-xs"><span class="text-muted">{{ 'kanban.moveTo' | translate }}</span>
                        <select class="rounded-md border border-border bg-card px-1 py-1" [disabled]="pending().has(t.id)" (change)="moveTo(t, $event)">
                          <option value="">—</option>
                          @for (s of targets(t); track s) { <option [value]="s">{{ 'tickets.status.' + pascal(s) | translate }}</option> }
                        </select></label>
                    }
                  </article>
                } @empty {
                  <p class="px-1 py-4 text-center text-xs text-muted">{{ 'kanban.empty' | translate }}</p>
                }
              </div>
              @if (col.tickets.length < col.total) {
                <p class="px-3 pb-2 text-xs text-muted">{{ 'kanban.more' | translate: { n: col.total - col.tickets.length } }}</p>
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

  protected readonly pascal = pascal;
  protected readonly priorities = ['Low', 'Medium', 'High', 'Urgent'];
  protected readonly board = signal<Column[] | null>(null);
  protected readonly error = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly pending = signal<ReadonlySet<string>>(new Set());
  protected readonly form = new FormGroup({
    search: new FormControl('', { nonNullable: true }),
    priority: new FormControl('', { nonNullable: true }),
    overdue: new FormControl(this.route.snapshot.queryParamMap.has('overdue'), { nonNullable: true }),
  });
  private seq = 0;

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) untracked(() => this.load());
      else this.board.set(null);
    });
    merge(this.realtime.on('notification.created'), this.realtime.reconnected)
      .pipe(debounceTime(1000), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
  }

  protected load(): void {
    const { search, priority, overdue } = this.form.getRawValue();
    const id = ++this.seq;
    this.error.set(false);
    this.gql
      .query(KanbanBoardDocument, { perColumn: 20, filter: { search: search || null, priority: priority ? (priority.toUpperCase() as 'LOW') : null, overdue: overdue || null } })
      .subscribe({
        next: (r) => {
          if (id === this.seq && this.pending().size === 0) this.board.set(r.kanbanBoard); // never overwrite a move in flight
        },
        error: () => this.error.set(true),
      });
  }

  protected targets(t: BoardTicketFragment): string[] {
    return TARGETS[t.status] ?? [];
  }

  protected canMove(t: BoardTicketFragment): boolean {
    return this.targets(t).length > 0;
  }

  protected canEnter(status: string) {
    return (drag: CdkDrag<BoardTicketFragment>) => (TARGETS[drag.data.status] ?? []).includes(status);
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
      error: (e: unknown) => {
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        this.message.set(status === 409 ? 'kanban.errors.conflict' : status === 403 ? 'kanban.errors.forbidden' : status === 400 ? 'kanban.errors.invalid' : 'kanban.errors.generic');
        this.settle(t.id);
      },
    });
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
