import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { map } from 'rxjs';
import { TicketPriority } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { CustomersDocument } from '../../core/graphql/generated';
import { PRIORITY_BADGE, TICKET_PRIORITIES, fieldErrors, httpStatus } from './ticket-ui';
import { TicketEligibility } from './ticket-eligibility';
import { TicketsApi } from './tickets-api';

export const TITLE_MAX = 200;
export const DESCRIPTION_MAX = 4000;

/**
 * Creates a ticket with the fields the caller's role may set. The server applies the role rules (customers: own contact, no
 * priority; agents: self-assigned; managers: customer and assignees) and rejects anything else, so this form only mirrors them.
 */
@Component({
  selector: 'app-ticket-create',
  imports: [ReactiveFormsModule, TranslatePipe, RouterLink],
  templateUrl: './ticket-create.html',
  styleUrl: './ticket-create.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketCreate {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly gql = inject(GraphqlClient);
  private readonly router = inject(Router);
  protected readonly eligibility = inject(TicketEligibility);

  protected readonly priorities = TICKET_PRIORITIES;
  protected readonly priorityBadge = PRIORITY_BADGE;
  protected readonly titleMax = TITLE_MAX;
  protected readonly descriptionMax = DESCRIPTION_MAX;
  protected readonly isManager = computed(
    () => this.tenant.role() === 'TenantOwner' || this.tenant.role() === 'Manager',
  );

  /** Customers see the form only while the server says they may open tickets; direct navigation gets the same answer. */
  protected readonly gate = computed<'form' | 'blocked' | 'loading' | 'error'>(() => {
    if (this.tenant.role() !== 'Customer') return 'form';
    const state = this.eligibility.state();
    if (state !== 'ready') return state;
    return this.eligibility.canCreate() ? 'form' : 'blocked';
  });

  protected readonly categories = rxResource({
    params: () => this.tenant.tenantId() ?? undefined,
    stream: () => this.api.categories().pipe(map((c) => c.filter((x) => x.isActive))),
  });
  protected readonly customers = rxResource({
    params: () => (this.isManager() ? this.tenant.tenantId() : null) ?? undefined,
    stream: () =>
      this.gql
        .query(CustomersDocument, { isActive: true, pageSize: 100 })
        .pipe(map((r) => r.customers.items)),
  });
  protected readonly agents = rxResource({
    params: () => (this.isManager() ? this.tenant.tenantId() : null) ?? undefined,
    stream: () =>
      this.api.members().pipe(map((m) => m.filter((x) => x.role === 'Agent' && x.isActive))),
  });

  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string>>({});
  protected readonly form = new FormGroup({
    title: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.minLength(3), Validators.maxLength(TITLE_MAX)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(DESCRIPTION_MAX)],
    }),
    categoryId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    priority: new FormControl<TicketPriority>('Medium', { nonNullable: true }),
    customerId: new FormControl('', { nonNullable: true }),
  });
  protected readonly titleLength = toSignal(
    this.form.controls.title.valueChanges.pipe(map((v) => v.length)),
    { initialValue: 0 },
  );
  protected readonly descriptionLength = toSignal(
    this.form.controls.description.valueChanges.pipe(map((v) => v.length)),
    { initialValue: 0 },
  );

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
    const customer = this.tenant.role() === 'Customer';
    this.saving.set(true);
    this.error.set(null);
    this.serverErrors.set({});
    this.api
      .create({
        title: v.title.trim(),
        description: v.description.trim(),
        categoryId: v.categoryId,
        priority: customer ? null : v.priority,
        customerId: manager && v.customerId ? v.customerId : null,
        assigneeMembershipIds: manager && this.selected().size ? [...this.selected()] : null,
      })
      .subscribe({
        next: (t) => void this.router.navigate(['/tickets', t.summary.id]),
        error: (e: unknown) => {
          this.saving.set(false); // the typed input is kept for a correction or retry
          this.serverErrors.set(fieldErrors(e));
          const status = httpStatus(e);
          if (status === 403 && customer) this.eligibility.reload(); // the status may have changed while the form was open
          this.error.set(
            status === 403
              ? 'tickets.form.forbidden'
              : status === 400
                ? 'tickets.form.invalid'
                : 'tickets.form.error',
          );
        },
      });
  }
}
