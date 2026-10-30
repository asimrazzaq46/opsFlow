import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Api } from '../../core/api/generated/api';
import { createCustomer, setCustomerStatus } from '../../core/api/generated/functions';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { CustomersDocument, CustomersQuery } from '../../core/graphql/generated';

const PAGE_SIZE = 20;

/** Reads through GraphQL, writes through the REST commands (Owner/Manager only - the server enforces it). */
@Component({
  selector: 'app-customers',
  imports: [ReactiveFormsModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="text-2xl font-bold tracking-tight">{{ 'customers.title' | translate }}</h1>

    <form [formGroup]="create" (ngSubmit)="submit()" class="mt-6 grid gap-3 rounded-xl border border-border bg-card p-4 sm:grid-cols-4" novalidate>
      <label class="grid gap-1 text-sm"><span>{{ 'customers.name' | translate }}</span>
        <input formControlName="name" class="rounded-lg border border-border bg-card px-2 py-1" [attr.aria-invalid]="create.controls.name.invalid && create.controls.name.touched" /></label>
      <label class="grid gap-1 text-sm"><span>{{ 'customers.email' | translate }}</span>
        <input type="email" formControlName="email" class="rounded-lg border border-border bg-card px-2 py-1" [attr.aria-invalid]="create.controls.email.invalid && create.controls.email.touched" /></label>
      <label class="grid gap-1 text-sm"><span>{{ 'customers.phone' | translate }}</span>
        <input formControlName="phone" class="rounded-lg border border-border bg-card px-2 py-1" /></label>
      <button type="submit" [disabled]="saving()" class="self-end rounded-lg bg-brand px-3 py-1.5 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">{{ 'customers.add' | translate }}</button>
      @if (formError(); as e) { <p role="alert" class="text-sm text-danger sm:col-span-4">{{ e | translate }}</p> }
    </form>

    <form [formGroup]="filter" (ngSubmit)="go(1)" class="mt-6 flex items-end gap-3 text-sm">
      <label class="grid gap-1"><span class="text-muted">{{ 'filters.search' | translate }}</span>
        <input type="search" formControlName="search" class="rounded-lg border border-border bg-card px-2 py-1" /></label>
      <button type="submit" class="rounded-lg border border-border px-3 py-1.5">{{ 'filters.apply' | translate }}</button>
    </form>

    <div class="mt-4 overflow-x-auto" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">{{ 'customers.loadError' | translate }} <button type="button" class="underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (data(); as d) {
        <table class="w-full min-w-[32rem] text-left text-sm">
          <thead class="text-muted"><tr><th class="py-2">{{ 'customers.name' | translate }}</th><th>{{ 'customers.email' | translate }}</th><th>{{ 'customers.phone' | translate }}</th><th>{{ 'common.status' | translate }}</th><th></th></tr></thead>
          <tbody>
            @for (c of d.items; track c.id) {
              <tr class="border-t border-border">
                <td class="py-2 font-medium">{{ c.name }}</td><td>{{ c.email }}</td><td>{{ c.phone ?? '—' }}</td>
                <td>{{ (c.isActive ? 'common.active' : 'common.inactive') | translate }}</td>
                <td class="text-right"><button type="button" class="underline" (click)="toggle(c.id, !c.isActive)">{{ (c.isActive ? 'common.deactivate' : 'common.activate') | translate }}</button></td>
              </tr>
            } @empty { <tr><td colspan="5" class="py-4 text-muted">{{ 'customers.empty' | translate }}</td></tr> }
          </tbody>
        </table>
        <nav class="mt-4 flex items-center justify-between text-sm">
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="d.page <= 1" (click)="go(d.page - 1)">‹ {{ 'common.previous' | translate }}</button>
          <span>{{ d.page }} / {{ pages(d) }}</span>
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="d.page >= pages(d)" (click)="go(d.page + 1)">{{ 'common.next' | translate }} ›</button>
        </nav>
      } @else { <p class="text-muted">{{ 'common.loading' | translate }}</p> }
    </div>
  `,
})
export class Customers {
  private readonly gql = inject(GraphqlClient);
  private readonly api = inject(Api);
  protected readonly tenant = inject(TenantService);

  protected readonly data = signal<CustomersQuery['customers'] | null>(null);
  protected readonly error = signal(false);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly filter = new FormGroup({ search: new FormControl('', { nonNullable: true }) });
  protected readonly create = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    phone: new FormControl('', { nonNullable: true }),
  });
  private page = 1;

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) untracked(() => this.go(1));
      else this.data.set(null);
    });
  }

  protected pages = (d: NonNullable<CustomersQuery['customers']>) => Math.max(1, Math.ceil(d.totalCount / PAGE_SIZE));

  protected go(page: number): void {
    this.page = page;
    this.load();
  }

  protected load(): void {
    this.error.set(false);
    this.gql.query(CustomersDocument, { search: this.filter.controls.search.value || null, page: this.page, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => this.data.set(r.customers),
      error: () => this.error.set(true),
    });
  }

  protected submit(): void {
    this.create.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (this.create.invalid || !tenantId || this.saving()) return;
    const v = this.create.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api.invoke(createCustomer, { tenantId, body: { name: v.name, email: v.email, phone: v.phone || null, linkedUserId: null } }).subscribe({
      next: () => {
        this.saving.set(false);
        this.create.reset();
        this.go(1);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.formError.set(e instanceof HttpErrorResponse && e.status === 409 ? 'customers.duplicate' : e instanceof HttpErrorResponse && e.status === 403 ? 'common.forbidden' : 'customers.saveError');
      },
    });
  }

  protected toggle(customerId: string, isActive: boolean): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId) return;
    this.api.invoke(setCustomerStatus, { tenantId, customerId, body: { isActive } }).subscribe({ next: () => this.load(), error: () => this.formError.set('customers.saveError') });
  }
}
