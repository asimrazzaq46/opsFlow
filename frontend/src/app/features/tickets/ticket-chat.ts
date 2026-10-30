import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { TicketMessageDto } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { mergeBySequence } from './merge';
import { TicketsApi } from './tickets-api';

interface MessageEvent {
  tenantId: string;
  ticketId: string;
  message: TicketMessageDto;
}

const PAGE_SIZE = 30;

@Component({
  selector: 'app-ticket-chat',
  imports: [ReactiveFormsModule, TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rounded-xl border border-border bg-card p-4" [attr.aria-labelledby]="'chat-title'">
      <h2 id="chat-title" class="text-lg font-semibold">{{ 'chat.title' | translate }}</h2>

      @if (hasOlder()) {
        <button type="button" class="mt-3 text-sm underline" [disabled]="loading()" (click)="loadOlder()">{{ 'chat.loadOlder' | translate }}</button>
      }
      <div class="mt-3 max-h-96 space-y-3 overflow-y-auto" role="log" aria-live="polite" tabindex="0" [attr.aria-label]="'chat.title' | translate">
        @if (loading() && !messages().length) {
          <p class="text-muted">{{ 'common.loading' | translate }}</p>
        } @else if (error()) {
          <p role="alert" class="text-danger">{{ 'chat.error' | translate }}
            <button type="button" class="ml-2 underline" (click)="init()">{{ 'common.retry' | translate }}</button></p>
        } @else if (!messages().length) {
          <p class="text-muted">{{ 'chat.empty' | translate }}</p>
        }
        @for (m of messages(); track m.id) {
          <article class="rounded-lg px-3 py-2" [class]="mine(m) ? 'ml-8 bg-brand/10' : 'mr-8 bg-surface'">
            <header class="text-xs text-muted">
              {{ mine(m) ? ('chat.you' | translate) : m.senderDisplayName ?? ('chat.supportTeam' | translate) }} ·
              <time [attr.datetime]="m.createdAtUtc">{{ m.createdAtUtc | date: 'short' }}</time>
            </header>
            <p class="whitespace-pre-wrap break-words">{{ m.content }}</p>
          </article>
        }
      </div>

      <form class="mt-4" (ngSubmit)="send()">
        <label for="chat-input" class="block text-sm font-medium">{{ 'chat.label' | translate }}</label>
        <textarea id="chat-input" rows="3" maxlength="4000" [formControl]="text"
          class="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-brand"></textarea>
        @if (sendError()) {
          <p role="alert" class="mt-1 text-sm text-danger">{{ 'chat.sendError' | translate }}</p>
        }
        <button type="submit" [disabled]="text.invalid || sending()"
          class="mt-2 rounded-lg bg-brand px-4 py-2 font-semibold text-brand-fg disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
          {{ (sendError() ? 'common.retry' : sending() ? 'chat.sending' : 'chat.send') | translate }}
        </button>
      </form>
    </section>
  `,
})
export class TicketChat {
  readonly ticketId = input.required<string>();

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);

  protected readonly messages = signal<TicketMessageDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly sending = signal(false);
  protected readonly sendError = signal(false);
  protected readonly hasOlder = signal(false);
  protected readonly text = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(4000), Validators.pattern(/\S/)] });

  private olderPage = 1;
  private pendingId: string | null = null; // reused on retry so the server never stores a duplicate

  constructor() {
    const destroyRef = inject(DestroyRef);
    // editing the text after a failure is a NEW message: the idempotency key must not be reused for different content
    this.text.valueChanges.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => (this.pendingId = null));
    this.realtime.on<MessageEvent>('ticket.message.created').pipe(takeUntilDestroyed(destroyRef)).subscribe((e) => {
      if (e.tenantId === this.tenant.tenantId() && e.ticketId === this.ticketId()) this.merge([e.message]);
    });
    this.realtime.reconnected.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.sync());
    effect(() => {
      this.ticketId();
      untracked(() => this.init());
    });
  }

  protected mine(m: TicketMessageDto): boolean {
    return m.senderUserId !== null && m.senderUserId === this.auth.user()?.id;
  }

  protected init(): void {
    this.messages.set([]);
    this.olderPage = 1;
    this.loading.set(true);
    this.error.set(false);
    this.api.messages(this.ticketId(), { page: 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        this.merge(r.items);
        this.hasOlder.set(r.items.length < Number(r.totalCount));
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected loadOlder(): void {
    this.loading.set(true);
    this.api.messages(this.ticketId(), { page: this.olderPage + 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        this.olderPage++;
        this.merge(r.items);
        this.hasOlder.set(this.olderPage * PAGE_SIZE < Number(r.totalCount));
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  /** Catch-up after a reconnect: everything newer than the last message we hold. */
  private sync(): void {
    const last = this.messages().at(-1);
    this.api.messages(this.ticketId(), { afterSequence: last ? Number(last.sequence) : 0, pageSize: 100 }).subscribe({
      next: (r) => this.merge(r.items),
      error: () => this.error.set(true),
    });
  }

  protected send(): void {
    if (this.text.invalid || this.sending()) return;
    const clientId = (this.pendingId ??= crypto.randomUUID());
    this.sending.set(true);
    this.sendError.set(false);
    this.api.sendMessage(this.ticketId(), this.text.value.trim(), clientId).subscribe({
      next: (m) => {
        this.merge([m]);
        this.text.reset('', { emitEvent: false });
        this.pendingId = null;
        this.sending.set(false);
      },
      error: () => {
        this.sendError.set(true);
        this.sending.set(false);
      },
    });
  }

  private merge(incoming: TicketMessageDto[]): void {
    this.messages.update((list) => mergeBySequence(list, incoming));
  }
}
