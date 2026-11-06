import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, viewChild } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { map } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { redirectUnlessManager } from '../../core/auth/manager-access';
import { fieldErrors, httpStatus, isForbidden, isNotFound } from '../tickets/ticket-ui';
import { CustomersApi } from './customers-api';
import { PortalUserPicker } from './portal-user-picker/portal-user-picker';

export const NAME_MAX = 150;
export const EMAIL_MAX = 256;
export const PHONE_MAX = 50;

/** Maps a failed save to a translation key; the 409/400 problem titles distinguish the e-mail and portal-link rules. */
export function saveErrorKey(e: unknown): string {
  const status = httpStatus(e);
  const title = e instanceof HttpErrorResponse ? String((e.error as { title?: string } | null)?.title ?? '') : '';
  if (status === 409) return /linked/i.test(title) ? 'customers.linkConflict' : 'customers.duplicate';
  if (status === 400) return /Customer member/i.test(title) ? 'customers.invalidLink' : 'customers.invalid';
  if (status === 403) return 'common.forbidden';
  if (status === 404) return 'customers.notFound';
  return 'customers.saveError';
}

/** Create and edit share one form. The contact (who the customer is) is separate from the optional linked portal user (who may sign in). */
@Component({
  selector: 'app-customer-form',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe, PortalUserPicker],
  templateUrl: './customer-form.html',
  styleUrl: './customer-form.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CustomerForm {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(CustomersApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly nameMax = NAME_MAX;
  protected readonly emailMax = EMAIL_MAX;
  protected readonly phoneMax = PHONE_MAX;
  protected readonly id = this.route.snapshot.paramMap.get('id');
  protected readonly editing = this.id !== null;

  protected readonly form = new FormGroup({
    name: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.minLength(2), Validators.maxLength(NAME_MAX)],
    }),
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email, Validators.maxLength(EMAIL_MAX)],
    }),
    phone: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(PHONE_MAX)] }),
  });

  protected readonly saving = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string>>({});
  protected readonly selectedUserId = signal<string | null>(null);
  private populatedFor: string | null = null;

  /** The customer being edited; tagged with its workspace so a response for a previous one is ignored. */
  protected readonly customer = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId && this.id ? { tenantId, id: this.id } : undefined;
    },
    stream: ({ params }) =>
      this.api.get(params.id).pipe(map((customer) => ({ tenantId: params.tenantId, customer }))),
  });
  protected readonly loaded = computed(() => {
    const r = this.customer.hasValue() ? this.customer.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r.customer : null;
  });
  protected readonly loadError = computed(() => {
    if (this.customer.status() !== 'error') return null;
    const e = this.customer.error();
    return isNotFound(e) ? 'customers.notFound' : isForbidden(e) ? 'common.forbidden' : 'customers.loadError';
  });

  /** Only offered when the contact has no portal user yet: an existing link is never replaced silently. */
  protected readonly canPickUser = computed(() => !this.editing || (!!this.loaded() && !this.loaded()!.linkedUserId));
  protected readonly picker = viewChild<PortalUserPicker>('picker');

  constructor() {
    redirectUnlessManager();
    effect(() => {
      const c = this.loaded();
      if (!c || this.populatedFor === c.id) return;
      this.populatedFor = c.id;
      this.form.reset({ name: c.name, email: c.email, phone: c.phone ?? '' });
    });
  }

  protected invalid(name: 'name' | 'email' | 'phone'): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || c.dirty);
  }

  protected fieldMessage(name: 'name' | 'email' | 'phone', serverKey: string): string | null {
    const c = this.form.controls[name];
    if (c.hasError('required')) return 'validation.required';
    if (c.hasError('email')) return 'customers.emailInvalid';
    if (c.hasError('minlength') || c.hasError('maxlength')) return 'customers.lengthInvalid';
    return this.serverErrors()[serverKey] ? 'customers.invalid' : null;
  }

  protected select(userId: string | null): void {
    this.selectedUserId.set(userId);
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (this.form.invalid || !tenantId || this.saving()) return;
    const v = this.form.getRawValue();
    const linkedUserId = this.canPickUser() ? this.selectedUserId() : null; // null on update keeps the existing link
    this.saving.set(true);
    this.errorKey.set(null);
    this.serverErrors.set({});
    const request = this.id
      ? this.api.update(this.id, { name: v.name.trim(), linkedUserId })
      : this.api.create({ name: v.name.trim(), email: v.email.trim(), phone: v.phone.trim() || null, linkedUserId });
    request.subscribe({
      next: (saved) => {
        this.saving.set(false);
        void this.router.navigate(['/customers', saved.id], {
          queryParams: { notice: this.editing ? 'saved' : 'created' },
        });
      },
      error: (e: unknown) => {
        this.saving.set(false);
        if (tenantId !== this.tenant.tenantId()) return;
        this.serverErrors.set(fieldErrors(e));
        this.errorKey.set(saveErrorKey(e));
        if (httpStatus(e) === 400 || httpStatus(e) === 409) this.picker()?.reload();
      },
    });
  }
}
