import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { TicketNoteDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { mergeBySequence } from './merge';
import { TicketsApi } from './tickets-api';

interface NoteEvent {
  tenantId: string;
  ticketId: string;
  note: TicketNoteDto;
}

/** Staff-only. The parent renders it only for non-customers; the API rejects customers regardless. */
@Component({
  selector: 'app-ticket-notes',
  imports: [ReactiveFormsModule, TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rounded-xl border border-amber-500/40 bg-card p-4" aria-labelledby="notes-title">
      <h2 id="notes-title" class="text-lg font-semibold">{{ 'notes.title' | translate }}</h2>
      <p class="text-xs text-muted">{{ 'notes.hint' | translate }}</p>

      <div class="mt-3 max-h-72 space-y-3 overflow-y-auto" role="log" aria-live="polite" tabindex="0" [attr.aria-label]="'notes.title' | translate">
        @if (loading() && !notes().length) {
          <p class="text-muted">{{ 'common.loading' | translate }}</p>
        } @else if (error()) {
          <p role="alert" class="text-danger">{{ 'notes.error' | translate }}
            <button type="button" class="ml-2 underline" (click)="init()">{{ 'common.retry' | translate }}</button></p>
        } @else if (!notes().length) {
          <p class="text-muted">{{ 'notes.empty' | translate }}</p>
        }
        @for (n of notes(); track n.id) {
          <article class="rounded-lg bg-surface px-3 py-2">
            <header class="text-xs text-muted">{{ n.authorDisplayName }} · <time [attr.datetime]="n.createdAtUtc">{{ n.createdAtUtc | date: 'short' }}</time></header>
            <p class="whitespace-pre-wrap break-words">{{ n.content }}</p>
          </article>
        }
      </div>

      <form class="mt-4" (ngSubmit)="add()">
        <label for="note-input" class="block text-sm font-medium">{{ 'notes.label' | translate }}</label>
        <textarea id="note-input" rows="2" maxlength="4000" [formControl]="text"
          class="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-brand"></textarea>
        @if (saveError()) {
          <p role="alert" class="mt-1 text-sm text-danger">{{ 'notes.saveError' | translate }}</p>
        }
        <button type="submit" [disabled]="text.invalid || saving()"
          class="mt-2 rounded-lg bg-brand px-4 py-2 font-semibold text-brand-fg disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
          {{ 'notes.add' | translate }}
        </button>
      </form>
    </section>
  `,
})
export class TicketNotes {
  readonly ticketId = input.required<string>();

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);
  private readonly realtime = inject(RealtimeService);

  protected readonly notes = signal<TicketNoteDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly saving = signal(false);
  protected readonly saveError = signal(false);
  protected readonly text = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(4000), Validators.pattern(/\S/)] });
  private pendingId: string | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.text.valueChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => (this.pendingId = null));
    this.realtime.on<NoteEvent>('ticket.note.created').pipe(takeUntilDestroyed(destroyRef)).subscribe((e) => {
      if (e.tenantId === this.tenant.tenantId() && e.ticketId === this.ticketId()) this.merge([e.note]);
    });
    this.realtime.reconnected.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.init());
    effect(() => {
      this.ticketId();
      untracked(() => {
        this.notes.set([]);
        this.init();
      });
    });
  }

  protected init(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.notes(this.ticketId(), 1, 100).subscribe({
      next: (r) => {
        this.merge(r.items);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected add(): void {
    if (this.text.invalid || this.saving()) return;
    const clientId = (this.pendingId ??= crypto.randomUUID());
    this.saving.set(true);
    this.saveError.set(false);
    this.api.createNote(this.ticketId(), this.text.value.trim(), clientId).subscribe({
      next: (n) => {
        this.merge([n]);
        this.text.reset('', { emitEvent: false });
        this.pendingId = null;
        this.saving.set(false);
      },
      error: () => {
        this.saveError.set(true);
        this.saving.set(false);
      },
    });
  }

  private merge(incoming: TicketNoteDto[]): void {
    this.notes.update((list) => mergeBySequence(list, incoming));
  }
}
