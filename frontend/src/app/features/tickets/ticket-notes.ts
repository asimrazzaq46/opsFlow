import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
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
  imports: [FormsModule, ReactiveFormsModule, TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ticket-notes.html',
  styleUrl: './ticket-notes.css',
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
  protected readonly text = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(4000), Validators.pattern(/\S/)],
  });
  private pendingId: string | null = null;

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.text.valueChanges
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe(() => (this.pendingId = null));
    this.realtime
      .on<NoteEvent>('ticket.note.created')
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe((e) => {
        if (e.tenantId === this.tenant.tenantId() && e.ticketId === this.ticketId())
          this.merge([e.note]);
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
