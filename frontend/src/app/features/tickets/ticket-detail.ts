import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { TicketDetailDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketActions } from './ticket-actions';
import { TicketAttachments } from './ticket-attachments';
import { TicketChat } from './ticket-chat';
import { TicketNotes } from './ticket-notes';
import { TicketsApi } from './tickets-api';

@Component({
  selector: 'app-ticket-detail',
  imports: [RouterLink, TranslatePipe, DatePipe, TicketChat, TicketNotes, TicketAttachments, TicketActions],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a routerLink="/tickets" class="text-sm text-brand underline">‹ {{ 'tickets.back' | translate }}</a>
    @if (loading()) {
      <p class="mt-4 text-muted" aria-live="polite">{{ 'common.loading' | translate }}</p>
    } @else if (error()) {
      <p role="alert" class="mt-4 text-danger">{{ 'tickets.loadError' | translate }}
        <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
    } @else if (ticket(); as t) {
      <header class="mt-3">
        <p class="font-mono text-sm text-muted">{{ t.summary.displayNumber }}</p>
        <h1 class="text-2xl font-bold tracking-tight">{{ t.summary.title }}</h1>
        <p class="mt-1 flex flex-wrap gap-3 text-sm text-muted">
          <span>{{ 'tickets.status.' + t.summary.status | translate }}</span>
          <span>{{ 'tickets.priority.' + t.summary.priority | translate }}</span>
          <time [attr.datetime]="t.summary.updatedAtUtc">{{ t.summary.updatedAtUtc | date: 'short' }}</time>
        </p>
        <p class="mt-3 whitespace-pre-wrap break-words">{{ t.description }}</p>
      </header>
      <div class="mt-6"><app-ticket-actions [ticket]="t" (changed)="refresh()" /></div>
      <div class="mt-2 grid gap-6 lg:grid-cols-2">
        <app-ticket-chat [ticketId]="id()" />
        <div class="space-y-6">
          @if (tenant.isStaff()) {
            <app-ticket-notes [ticketId]="id()" />
          }
          <app-ticket-attachments [ticketId]="id()" [staff]="tenant.isStaff()" />
        </div>
      </div>
    }
  `,
})
export class TicketDetail {
  readonly id = input.required<string>();

  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly router = inject(Router);

  protected readonly ticket = signal<TicketDetailDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  private readonly openedIn = this.tenant.tenantId();

  constructor() {
    effect(() => {
      // a ticket belongs to ONE workspace: switching workspaces leaves the page
      if (this.tenant.tenantId() !== this.openedIn) void this.router.navigateByUrl('/tickets');
    });
    effect(() => {
      this.id();
      untracked(() => this.load());
    });
  }

  /** Silent reload after a command: keeps chat/notes mounted. */
  protected refresh(): void {
    this.api.get(this.id()).subscribe((t) => this.ticket.set(t));
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.get(this.id()).subscribe({
      next: (t) => {
        this.ticket.set(t);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
