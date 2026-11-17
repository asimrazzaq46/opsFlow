import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import { getTenantSettings, updateTenantSettings } from '../../core/api/generated/functions';
import { TenantSettingsDto } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { UnsavedChanges } from '../../core/auth/unsaved-changes';
import { requestErrorKey } from '../auth/auth-api';
import { ConfirmDialog } from '../../shared/components/confirm-dialog/confirm-dialog';

/** Same rule as the backend's display-name validation: 2-100 characters, no control or invisible formatting characters. */
const NAME_MIN = 2;
const NAME_MAX = 100;
const nameRules = (c: AbstractControl<string>): ValidationErrors | null => {
  const v = c.value.trim();
  if (v.length < NAME_MIN || v.length > NAME_MAX) return { length: true };
  return /[\p{Cc}\p{Cf}]/u.test(v) ? { chars: true } : null;
};

/** Browser-supplied IANA ids plus UTC, which the backend accepts but `Intl` may omit. */
function supportedZones(): string[] {
  const zones = new Set<string>(['UTC']);
  try {
    for (const z of Intl.supportedValuesOf('timeZone')) zones.add(z);
  } catch {
    /* older engines: UTC and the configured zone remain selectable */
  }
  return [...zones];
}

/** The IANA id stays first so native type-ahead in the select matches it; the offset is only a readable hint. */
function zoneLabel(id: string): string {
  try {
    const part = new Intl.DateTimeFormat('en', { timeZone: id, timeZoneName: 'shortOffset' })
      .formatToParts(new Date())
      .find((p) => p.type === 'timeZoneName');
    return part ? `${id} (${part.value})` : id;
  } catch {
    return id;
  }
}

/**
 * Organization settings of the active workspace. Everything shown comes from GET; Owners may change the name and the reporting
 * timezone (PUT, Owner-only on the server). Managers and Agents see the same values read-only. The role here only shapes the UI.
 */
@Component({
  selector: 'app-organization-settings',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog],
  templateUrl: './organization-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:beforeunload)': 'warnOnUnload($event)' },
})
export class OrganizationSettings {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly unsaved = inject(UnsavedChanges);
  protected readonly tenant = inject(TenantService);

  private readonly leaveDialog = viewChild.required<ConfirmDialog>('leaveDialog');

  /** Persisted server values; the form is only ever filled from these. */
  protected readonly data = signal<TenantSettingsDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  /** Translation key of the last save outcome. */
  protected readonly saveError = signal<string | null>(null);
  protected readonly conflict = signal(false);
  protected readonly saved = signal(false);
  /** Messages returned by the server for a field (400 ProblemDetails), keyed by form control. */
  protected readonly serverErrors = signal<{ name?: string; reportingTimeZone?: string }>({});
  protected readonly dirty = signal(false);

  protected readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [nameRules] }),
    reportingTimeZone: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  private readonly browserZones = supportedZones().map((id) => ({ id, label: zoneLabel(id) }));
  /** The saved zone stays selectable even when this browser does not list it. */
  protected readonly zones = computed(() => {
    const current = this.data()?.reportingTimeZone;
    return current && !this.browserZones.some((z) => z.id === current)
      ? [{ id: current, label: current }, ...this.browserZones]
      : this.browserZones;
  });
  /** Read-only by default; only an Owner can enter edit mode. */
  protected readonly editing = signal(false);
  private pendingCancel = false;

  protected readonly canEdit = computed(() => this.tenant.isOwner());
  protected readonly canView = computed(() => this.tenant.isStaff());

  private request?: Subscription;
  /** Tenant the form content belongs to; a different active tenant means any edit is stale and already reset. */
  private loadedFor: string | null = null;
  private leaveResolve?: (ok: boolean) => void;

  constructor() {
    // Workspace or role changes reset everything that belonged to the previous one and cancel its in-flight request.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      const canView = this.canView();
      untracked(() => {
        this.request?.unsubscribe();
        this.loadedFor = null;
        this.data.set(null);
        this.loadError.set(null);
        this.resetMessages();
        this.busy.set(false);
        this.setDirty(false);
        this.editing.set(false);
        this.form.reset({ name: '', reportingTimeZone: '' }, { emitEvent: false });
        if (tenantId && canView) this.load();
      });
    });
    effect(() => {
      const edit = this.canEdit();
      untracked(() => {
        if (edit) return this.form.enable({ emitEvent: false });
        this.form.disable({ emitEvent: false });
        this.exitEdit(); // the role was lost: the server would refuse the save anyway
      });
    });
    this.form.valueChanges.subscribe(() => {
      const d = this.data();
      this.setDirty(
        !!d &&
          (this.form.controls.name.value.trim() !== d.name || this.form.controls.reportingTimeZone.value.trim() !== d.reportingTimeZone),
      );
      this.saved.set(false);
      this.serverErrors.set({});
    });
    inject(DestroyRef).onDestroy(() => {
      this.request?.unsubscribe();
      this.unsaved.dirty.set(false);
    });
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId) return;
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.request = this.api.invoke(getTenantSettings, { tenantId }).subscribe({
      next: (dto) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.loading.set(false);
        this.loadedFor = tenantId;
        this.apply(dto);
        this.resetMessages();
      },
      error: (err: unknown) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.loading.set(false);
        this.loadError.set(this.errorKey(err, 'settings.loadError'));
      },
    });
  }

  protected save(): void {
    const tenantId = this.tenant.tenantId();
    this.form.markAllAsTouched();
    if (!tenantId || !this.canEdit() || !this.editing() || this.busy() || !this.dirty() || this.form.invalid) return;
    const { name, reportingTimeZone } = this.form.getRawValue();
    this.busy.set(true);
    this.resetMessages();
    this.request = this.api
      .invoke(updateTenantSettings, { tenantId, body: { name: name.trim(), reportingTimeZone: reportingTimeZone.trim() } })
      .subscribe({
        next: (dto) => {
          if (tenantId !== this.tenant.tenantId()) return;
          const renamed = dto.name !== this.data()?.name;
          this.busy.set(false);
          this.apply(dto);
          this.editing.set(false);
          this.saved.set(true);
          // The sidebar, selector and page headers read the name from the loaded memberships.
          if (renamed) this.tenant.load().subscribe({ error: () => undefined });
        },
        error: (err: unknown) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.busy.set(false);
          this.fail(err);
        },
      });
  }

  protected startEdit(): void {
    if (!this.canEdit() || !this.data()) return;
    this.resetMessages();
    this.editing.set(true);
  }

  /** Leaves edit mode; edits that were not saved need an explicit confirmation first. */
  protected cancel(): void {
    if (this.busy()) return;
    if (this.dirty()) {
      this.pendingCancel = true;
      this.leaveDialog().open();
      return;
    }
    this.exitEdit();
  }

  private exitEdit(): void {
    const d = this.data();
    if (d) this.apply(d);
    this.resetMessages();
    this.editing.set(false);
  }

  /** Conflict recovery: replaces the form with the server's current values (an explicit user action). */
  protected reload(): void {
    this.resetMessages();
    this.load();
  }

  /** Route guard hook: resolves true when there is nothing to lose or the user confirms discarding. */
  confirmLeave(): boolean | Promise<boolean> {
    // A signed-out session or a different workspace has nothing left to protect.
    if (!this.dirty() || this.loadedFor !== this.tenant.tenantId() || !this.auth.isAuthenticated()) return true;
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

  private apply(dto: TenantSettingsDto): void {
    this.data.set(dto);
    this.form.reset({ name: dto.name, reportingTimeZone: dto.reportingTimeZone }, { emitEvent: false });
    this.setDirty(false);
  }

  private setDirty(value: boolean): void {
    this.dirty.set(value);
    this.unsaved.dirty.set(value);
  }

  private resetMessages(): void {
    this.saved.set(false);
    this.saveError.set(null);
    this.conflict.set(false);
    this.serverErrors.set({});
  }

  private fail(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 400) {
      const errors = (err.error?.errors ?? {}) as Record<string, string[] | undefined>;
      const name = errors['Name']?.[0];
      const zone = errors['ReportingTimeZone']?.[0];
      if (name || zone) {
        this.serverErrors.set({ name, reportingTimeZone: zone });
        return;
      }
    }
    if (err instanceof HttpErrorResponse && err.status === 409) {
      this.conflict.set(true);
      return;
    }
    this.saveError.set(this.errorKey(err, 'settings.saveError'));
  }

  private errorKey(err: unknown, fallback: string): string {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 403) return 'common.forbidden';
      if (err.status === 404) return 'settings.notFound';
    }
    return requestErrorKey(err, fallback);
  }
}
