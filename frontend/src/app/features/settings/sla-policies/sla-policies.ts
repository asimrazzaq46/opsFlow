import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, map, startWith, timeout } from 'rxjs';
import { Api } from '../../../core/api/generated/api';
import {
  createSlaPolicy,
  listSlaPolicies,
  updateSlaPolicy,
} from '../../../core/api/generated/functions';
import { SlaPolicyDto, TicketPriority } from '../../../core/api/generated/models';
import { AuthService } from '../../../core/auth/auth.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { UnsavedChanges } from '../../../core/auth/unsaved-changes';
import { LanguageService } from '../../../i18n/language.service';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';
import { requestErrorKey } from '../../auth/auth-api';
import { TICKET_PRIORITIES } from '../../tickets/ticket-ui';
import {
  SLA_UNITS,
  SlaUnit,
  splitMinutes,
  targetError,
  toMinutes,
} from './sla-duration';

/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;

const PRIORITY_DOT: Record<TicketPriority, string> = {
  Low: 'bg-outline',
  Medium: 'bg-secondary',
  High: 'bg-tertiary',
  Urgent: 'bg-error',
};

interface Feedback {
  key: string;
  error: boolean;
  params?: Record<string, string>;
}

/**
 * SLA targets per priority of the active workspace, in calendar minutes exactly as stored. Staff read them; Owners and Managers edit one
 * priority at a time (or create a genuinely missing one). The server authorizes every request again and its SLA engine is untouched:
 * a change only affects tickets created afterwards, existing cycles keep their snapshot.
 */
@Component({
  selector: 'app-sla-policies',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog],
  templateUrl: './sla-policies.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:beforeunload)': 'warnOnUnload($event)' },
})
export class SlaPolicies {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly lang = inject(LanguageService);
  private readonly unsaved = inject(UnsavedChanges);
  private readonly injector = inject(Injector);
  protected readonly tenant = inject(TenantService);

  private readonly leaveDialog = viewChild.required<ConfirmDialog>('leaveDialog');

  protected readonly units = SLA_UNITS;
  protected readonly dot = PRIORITY_DOT;

  protected readonly items = signal<SlaPolicyDto[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly message = signal<Feedback | null>(null);
  protected readonly saveError = signal<string | null>(null);
  protected readonly busy = signal(false);
  /** Priority whose row is being edited; only one at a time. */
  protected readonly editing = signal<TicketPriority | null>(null);
  protected readonly submitted = signal(false);

  protected readonly canView = computed(() => this.tenant.isStaff());
  protected readonly canEdit = computed(() => this.tenant.isManager());

  /** The active policy per priority; a priority without one is genuinely Missing (never filled with a default). */
  protected readonly rows = computed(() =>
    this.items()
      ? TICKET_PRIORITIES.map((priority) => ({ priority, policy: this.policyFor(priority) }))
      : null,
  );

  protected readonly form = new FormGroup({
    responseAmount: new FormControl<number | null>(null),
    responseUnit: new FormControl<SlaUnit>('hours', { nonNullable: true }),
    resolutionAmount: new FormControl<number | null>(null),
    resolutionUnit: new FormControl<SlaUnit>('hours', { nonNullable: true }),
  });
  private readonly value = toSignal(
    this.form.valueChanges.pipe(
      startWith(null),
      map(() => this.form.getRawValue()),
    ),
    { requireSync: true },
  );
  private readonly responseMinutes = computed(() =>
    toMinutes(this.value().responseAmount, this.value().responseUnit),
  );
  private readonly resolutionMinutes = computed(() =>
    toMinutes(this.value().resolutionAmount, this.value().resolutionUnit),
  );

  /** Hints appear once something was typed (or after a save attempt), so a fresh form is not covered in errors. */
  protected readonly responseError = computed(() =>
    this.value().responseAmount === null && !this.submitted() ? null : targetError(this.responseMinutes()),
  );
  protected readonly resolutionError = computed<'invalid' | 'range' | 'order' | null>(() => {
    if (this.value().resolutionAmount === null && !this.submitted()) return null;
    const resolution = this.resolutionMinutes();
    const own = targetError(resolution);
    if (own) return own;
    const response = this.responseMinutes();
    return resolution !== null && response !== null && resolution < response ? 'order' : null;
  });
  private readonly valid = computed(() => {
    const response = this.responseMinutes();
    const resolution = this.resolutionMinutes();
    return (
      targetError(response) === null &&
      targetError(resolution) === null &&
      resolution! >= response!
    );
  });

  protected readonly dirty = computed(() => {
    const priority = this.editing();
    if (!priority) return false;
    const policy = this.policyFor(priority);
    const v = this.value();
    return policy
      ? this.responseMinutes() !== Number(policy.responseMinutes) ||
          this.resolutionMinutes() !== Number(policy.resolutionMinutes)
      : v.responseAmount !== null || v.resolutionAmount !== null;
  });

  private readonly formats = computed(() => {
    const locale = this.lang.current();
    const make = (unit: string, unitDisplay: 'long' | 'short') =>
      new Intl.NumberFormat(locale, { style: 'unit', unit, unitDisplay });
    return {
      minutes: make('minute', 'long'),
      hours: make('hour', 'long'),
      days: make('day', 'long'),
      shortMinutes: make('minute', 'short'),
    };
  });

  private request?: Subscription;
  private pendingCancel = false;
  private leaveResolve?: (ok: boolean) => void;

  constructor() {
    // A workspace switch discards everything that belonged to the previous one and cancels its in-flight request.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      const canView = this.canView();
      untracked(() => {
        this.request?.unsubscribe();
        this.items.set(null);
        this.loadError.set(null);
        this.message.set(null);
        this.busy.set(false);
        this.leaveDialog().close();
        this.exitEdit();
        if (tenantId && canView) this.load();
      });
    });
    // The server would refuse the save anyway once the role is lost.
    effect(() => {
      if (!this.canEdit()) untracked(() => this.exitEdit());
    });
    // The shell reads this to warn before a workspace switch or sign-out.
    effect(() => this.unsaved.dirty.set(this.dirty()));
    inject(DestroyRef).onDestroy(() => {
      this.request?.unsubscribe();
      this.unsaved.dirty.set(false);
    });
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.canView()) return;
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.request = this.api
      .invoke(listSlaPolicies, { tenantId })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (list) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.items.set(list);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.loading.set(false);
          this.loadError.set(
            e instanceof HttpErrorResponse && e.status === 403
              ? 'common.forbidden'
              : requestErrorKey(e, 'settings.sla.loadError'),
          );
        },
      });
  }

  protected startEdit(priority: TicketPriority): void {
    if (!this.canEdit() || this.editing() || this.busy()) return;
    const policy = this.policyFor(priority);
    const response = policy ? splitMinutes(Number(policy.responseMinutes)) : null;
    const resolution = policy ? splitMinutes(Number(policy.resolutionMinutes)) : null;
    this.form.reset({
      responseAmount: response?.amount ?? null,
      responseUnit: response?.unit ?? 'hours',
      resolutionAmount: resolution?.amount ?? null,
      resolutionUnit: resolution?.unit ?? 'hours',
    });
    this.message.set(null);
    this.saveError.set(null);
    this.submitted.set(false);
    this.editing.set(priority);
    this.focusSoon('sla-response-amount');
  }

  protected save(): void {
    const tenantId = this.tenant.tenantId();
    const priority = this.editing();
    this.submitted.set(true);
    if (!tenantId || !priority || !this.canEdit() || this.busy() || !this.valid()) return;
    const policy = this.policyFor(priority);
    if (policy && !this.dirty()) return this.exitEdit();
    const body = {
      responseMinutes: this.responseMinutes()!,
      resolutionMinutes: this.resolutionMinutes()!,
    };
    // Decided at save time: after a conflicting reload a "missing" row may already have a policy.
    const call: Observable<SlaPolicyDto> = policy
      ? this.api.invoke(updateSlaPolicy, { tenantId, policyId: policy.id, body })
      : this.api.invoke(createSlaPolicy, { tenantId, body: { priority, ...body } });
    this.busy.set(true);
    this.saveError.set(null);
    this.message.set(null);
    call.pipe(timeout(REQUEST_TIMEOUT_MS)).subscribe({
      next: (saved) => {
        this.busy.set(false);
        if (tenantId !== this.tenant.tenantId()) return;
        this.items.update((list) => [...(list ?? []).filter((p) => p.id !== saved.id), saved]);
        this.exitEdit();
        this.focusSoon(`sla-edit-${priority}`);
        this.message.set({ key: 'settings.sla.saved', error: false, params: { priority } });
        this.load(); // confirm with what the server persisted
      },
      error: (e: unknown) => {
        this.busy.set(false);
        if (tenantId !== this.tenant.tenantId()) return;
        this.saveError.set(this.errorKey(e, policy ? 'update' : 'create'));
        // The input stays; a vanished or concurrently created policy needs fresh data behind it.
        if (e instanceof HttpErrorResponse && (e.status === 404 || e.status === 409)) this.load();
      },
    });
  }

  /** Leaves edit mode; unsaved input needs an explicit confirmation first. */
  protected cancel(): void {
    const editing = this.editing();
    if (this.busy() || !editing) return;
    if (this.dirty()) {
      this.pendingCancel = true;
      this.leaveDialog().open();
      return;
    }
    this.exitEdit();
    this.focusSoon(`sla-edit-${editing}`);
  }

  /** Route guard hook: resolves true when there is nothing to lose or the user confirms discarding. */
  confirmLeave(): boolean | Promise<boolean> {
    if (!this.dirty() || !this.auth.isAuthenticated()) return true;
    return new Promise<boolean>((resolve) => {
      this.leaveResolve = resolve;
      this.leaveDialog().open();
    });
  }

  protected leaveConfirmed(): void {
    if (this.pendingCancel) {
      this.pendingCancel = false;
      this.exitEdit();
    }
    this.leaveResolve?.(true);
    this.leaveResolve = undefined;
    this.leaveDialog().close();
  }

  protected leaveClosed(): void {
    this.pendingCancel = false;
    this.leaveResolve?.(false);
    this.leaveResolve = undefined;
  }

  protected warnOnUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) event.preventDefault();
  }

  /** Localized long form of the stored minutes, using the largest unit that represents them exactly. */
  protected duration(minutes: number | string): string {
    const { amount, unit } = splitMinutes(Number(minutes));
    return this.formats()[unit].format(amount);
  }

  /** Exact stored minutes, only worth showing when the main label uses a larger unit. */
  protected exact(minutes: number | string): string | null {
    const value = Number(minutes);
    return splitMinutes(value).unit === 'minutes' ? null : this.formats().shortMinutes.format(value);
  }

  private policyFor(priority: TicketPriority): SlaPolicyDto | null {
    return this.items()?.find((p) => p.priority === priority && p.isActive) ?? null;
  }

  /** Keyboard users keep their place: the dialog-less editor opens on its first field and closes on its row's button. */
  private focusSoon(id: string): void {
    afterNextRender(() => document.getElementById(id)?.focus(), { injector: this.injector });
  }

  private exitEdit(): void {
    this.editing.set(null);
    this.submitted.set(false);
    this.saveError.set(null);
  }

  private errorKey(e: unknown, operation: 'create' | 'update'): string {
    if (e instanceof HttpErrorResponse) {
      if (e.status === 400) return 'settings.sla.errors.invalid';
      if (e.status === 403) return 'common.forbidden';
      if (e.status === 404) return 'settings.sla.errors.notFound';
      if (e.status === 409) return `settings.sla.errors.${operation === 'create' ? 'exists' : 'conflict'}`;
    }
    return requestErrorKey(e, 'settings.sla.errors.generic');
  }
}
