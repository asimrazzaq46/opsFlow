import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { TicketSummaryDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketsApi } from './tickets-api';

const PAGE_SIZE = 20;

@Component({
  selector: 'app-ticket-list',
  imports: [RouterLink, TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="text-2xl font-bold tracking-tight">{{ 'tickets.title' | translate }}</h1>
    <div class="mt-6" aria-live="polite">
      @if (!tenant.tenantId()) {
        <p class="text-muted">{{ (tenant.loading() ? 'common.loading' : 'tenant.none') | translate }}</p>
      } @else if (error()) {
        <p role="alert" class="text-danger">{{ 'tickets.loadError' | translate }}
          <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (loading() && !items().length) {
        <p class="text-muted">{{ 'common.loading' | translate }}</p>
      } @else if (!items().length) {
        <p class="text-muted">{{ 'tickets.empty' | translate }}</p>
      } @else {
        <ul class="divide-y divide-border rounded-xl border border-border bg-card">
          @for (t of items(); track t.id) {
            <li>
              <a [routerLink]="['/tickets', t.id]" class="flex flex-wrap items-center justify-between gap-2 px-4 py-3 hover:bg-surface focus-visible:outline-2 focus-visible:outline-brand">
                <span><span class="font-mono text-sm text-muted">{{ t.displayNumber }}</span> <span class="font-medium">{{ t.title }}</span></span>
                <span class="flex items-center gap-3 text-xs text-muted">
                  <span>{{ 'tickets.status.' + t.status | translate }}</span>
                  <span>{{ 'tickets.priority.' + t.priority | translate }}</span>
                  <time [attr.datetime]="t.updatedAtUtc">{{ t.updatedAtUtc | date: 'short' }}</time>
                </span>
              </a>
            </li>
          }
        </ul>
        <nav class="mt-4 flex items-center justify-between text-sm" [attr.aria-label]="'tickets.paging' | translate">
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="page() <= 1" (click)="go(page() - 1)">‹ {{ 'common.previous' | translate }}</button>
          <span>{{ page() }} / {{ pages() }}</span>
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="page() >= pages()" (click)="go(page() + 1)">{{ 'common.next' | translate }} ›</button>
        </nav>
      }
    </div>
  `,
})
export class TicketList {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);

  protected readonly items = signal<TicketSummaryDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly page = signal(1);
  protected readonly pages = signal(1);

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) untracked(() => this.go(1));
    });
  }

  protected go(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    this.loading.set(true);
    this.error.set(false);
    this.api.list(this.page(), PAGE_SIZE).subscribe({
      next: (r) => {
        if (this.tenant.tenantId() !== tenantId) return;
        this.items.set(r.items);
        this.pages.set(Math.max(1, Math.ceil(Number(r.totalCount) / PAGE_SIZE)));
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
