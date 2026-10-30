import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { MemberDto, TicketCategoryDto, TicketPriority } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { CustomersDocument } from '../../core/graphql/generated';
import { TicketsApi } from './tickets-api';

const PRIORITIES: TicketPriority[] = ['Low', 'Medium', 'High', 'Urgent'];

@Component({
  selector: 'app-ticket-create',
  imports: [ReactiveFormsModule, TranslatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a routerLink="/tickets" class="text-sm text-brand underline">‹ {{ 'tickets.back' | translate }}</a>
    <h1 class="mt-3 text-2xl font-bold tracking-tight">{{ 'tickets.new' | translate }}</h1>
    <form [formGroup]="form" (ngSubmit)="submit()" class="mt-6 grid max-w-2xl gap-4" novalidate>
      <label class="grid gap-1 text-sm"><span class="font-medium">{{ 'tickets.form.title' | translate }}</span>
        <input formControlName="title" maxlength="200" class="rounded-lg border border-border bg-card px-3 py-2" [attr.aria-invalid]="invalid('title')" aria-describedby="title-err" />
        @if (invalid('title')) { <span id="title-err" class="text-danger">{{ 'validation.required' | translate }}</span> }
        @if (fieldErrors()['Title']; as e) { <span class="text-danger">{{ e }}</span> }</label>
      <label class="grid gap-1 text-sm"><span class="font-medium">{{ 'tickets.form.description' | translate }}</span>
        <textarea formControlName="description" rows="6" class="rounded-lg border border-border bg-card px-3 py-2" [attr.aria-invalid]="invalid('description')"></textarea>
        @if (invalid('description')) { <span class="text-danger">{{ 'validation.required' | translate }}</span> }
        @if (fieldErrors()['Description']; as e) { <span class="text-danger">{{ e }}</span> }</label>
      <label class="grid gap-1 text-sm"><span class="font-medium">{{ 'tickets.form.category' | translate }}</span>
        <select formControlName="categoryId" class="rounded-lg border border-border bg-card px-3 py-2">
          <option value="">—</option>
          @for (c of categories(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
        </select>
        @if (invalid('categoryId')) { <span class="text-danger">{{ 'validation.required' | translate }}</span> }</label>

      @if (tenant.role() !== 'Customer') {
        <label class="grid gap-1 text-sm"><span class="font-medium">{{ 'tickets.form.priority' | translate }}</span>
          <select formControlName="priority" class="rounded-lg border border-border bg-card px-3 py-2">
            @for (p of priorities; track p) { <option [value]="p">{{ 'tickets.priority.' + p | translate }}</option> }
          </select></label>
      }
      @if (isManager()) {
        <label class="grid gap-1 text-sm"><span class="font-medium">{{ 'tickets.form.customer' | translate }}</span>
          <select formControlName="customerId" class="rounded-lg border border-border bg-card px-3 py-2">
            <option value="">—</option>
            @for (c of customers(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }
          </select></label>
        <fieldset class="grid gap-1 text-sm"><legend class="font-medium">{{ 'tickets.form.assignees' | translate }}</legend>
          <div class="flex flex-wrap gap-3">
            @for (a of agents(); track a.id) {
              <label class="flex items-center gap-2"><input type="checkbox" [checked]="selected().has(a.id)" (change)="toggle(a.id)" /> {{ a.displayName }}</label>
            } @empty { <span class="text-muted">{{ 'common.noData' | translate }}</span> }
          </div></fieldset>
      }

      @if (error(); as e) { <p role="alert" class="text-danger">{{ e | translate }}</p> }
      <div class="flex gap-3">
        <button type="submit" [disabled]="saving()" class="rounded-lg bg-brand px-4 py-2 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">
          {{ (saving() ? 'common.saving' : 'tickets.form.submit') | translate }}</button>
        <a routerLink="/tickets" class="rounded-lg border border-border px-4 py-2">{{ 'common.cancel' | translate }}</a>
      </div>
    </form>
  `,
})
export class TicketCreate {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly gql = inject(GraphqlClient);
  private readonly router = inject(Router);

  protected readonly priorities = PRIORITIES;
  protected readonly categories = signal<TicketCategoryDto[]>([]);
  protected readonly customers = signal<{ id: string; name: string }[]>([]);
  protected readonly agents = signal<MemberDto[]>([]);
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string>>({});
  protected readonly form = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    categoryId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    priority: new FormControl<TicketPriority>('Medium', { nonNullable: true }),
    customerId: new FormControl('', { nonNullable: true }),
  });

  protected isManager = () => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager';

  constructor() {
    effect(() => {
      if (!this.tenant.tenantId()) return;
      untracked(() => {
        this.api.categories().subscribe((c) => this.categories.set(c.filter((x) => x.isActive)));
        if (this.isManager()) {
          this.gql.query(CustomersDocument, { isActive: true, pageSize: 100 }).subscribe((r) => this.customers.set(r.customers.items));
          this.api.members().subscribe((m) => this.agents.set(m.filter((x) => x.role === 'Agent' && x.isActive)));
        }
      });
    });
  }

  protected invalid(name: 'title' | 'description' | 'categoryId'): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || c.dirty);
  }

  protected toggle(id: string): void {
    this.selected.update((s) => {
      const next = new Set(s);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    const v = this.form.getRawValue();
    const manager = this.isManager();
    this.saving.set(true);
    this.error.set(null);
    this.fieldErrors.set({});
    this.api
      .create({
        title: v.title,
        description: v.description,
        categoryId: v.categoryId,
        priority: this.tenant.role() === 'Customer' ? null : v.priority,
        customerId: manager && v.customerId ? v.customerId : null,
        assigneeMembershipIds: manager && this.selected().size ? [...this.selected()] : null,
      })
      .subscribe({
        next: (t) => void this.router.navigate(['/tickets', t.summary.id]),
        error: (e: unknown) => {
          this.saving.set(false); // the typed input is kept
          if (e instanceof HttpErrorResponse && e.status === 400 && e.error?.errors) {
            this.fieldErrors.set(Object.fromEntries(Object.entries(e.error.errors as Record<string, string[]>).map(([k, m]) => [k, m[0]])));
          }
          this.error.set(e instanceof HttpErrorResponse && e.status === 403 ? 'common.forbidden' : 'tickets.form.error');
        },
      });
  }
}
