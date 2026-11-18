import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, timeout } from 'rxjs';
import { Api } from '../../../core/api/generated/api';
import {
  createTicketCategory,
  listTicketCategories,
  renameTicketCategory,
  setTicketCategoryStatus,
} from '../../../core/api/generated/functions';
import { TicketCategoryDto } from '../../../core/api/generated/models';
import { TenantService } from '../../../core/auth/tenant.service';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';
import { requestErrorKey } from '../../auth/auth-api';

/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;
/** Same bounds as the server's request validation, applied to the trimmed value that is actually sent. */
const NAME_MIN = 2;
const NAME_MAX = 100;
const nameRules = (c: AbstractControl<string>): ValidationErrors | null => {
  const length = c.value.trim().length;
  return length < NAME_MIN || length > NAME_MAX ? { length: true } : null;
};

type Filter = 'active' | 'all';
type Operation = 'create' | 'rename' | 'status';

interface Feedback {
  key: string;
  error: boolean;
  params?: Record<string, string>;
}

/**
 * Category management of the active workspace (Owners and Managers; the server authorizes every request again). The list is always
 * what the server returns: every successful change reloads it, so names, status and the Active/All filter never drift from the
 * server. Ticket forms and filters load their own list when they open, so they pick the change up without any shared state.
 */
@Component({
  selector: 'app-ticket-categories',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog, NgTemplateOutlet],
  templateUrl: './ticket-categories.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketCategories {
  private readonly api = inject(Api);
  protected readonly tenant = inject(TenantService);

  private readonly nameDialog = viewChild.required<ElementRef<HTMLDialogElement>>('nameDialog');
  private readonly statusDialog = viewChild.required<ConfirmDialog>('statusDialog');
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  protected readonly filters: readonly Filter[] = ['active', 'all'];
  protected readonly filter = signal<Filter>('active');
  protected readonly items = signal<TicketCategoryDto[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly message = signal<Feedback | null>(null);
  /** Categories with a status request in flight, so a row can never be submitted twice. */
  protected readonly rowBusy = signal<Record<string, true>>({});

  /** Create/rename dialog: a null target means create. */
  protected readonly target = signal<TicketCategoryDto | null>(null);
  protected readonly name = new FormControl('', { nonNullable: true, validators: [nameRules] });
  protected readonly saving = signal(false);
  protected readonly dialogError = signal<string | null>(null);

  protected readonly statusTarget = signal<TicketCategoryDto | null>(null);
  protected readonly statusBusy = computed(() => {
    const t = this.statusTarget();
    return !!t && !!this.rowBusy()[t.id];
  });

  private request?: Subscription;
  private refocusAfterLoad = false;

  constructor() {
    // A workspace switch discards everything that belonged to the previous one and cancels its in-flight request.
    effect(() => {
      this.tenant.tenantId();
      untracked(() => {
        this.request?.unsubscribe();
        this.items.set(null);
        this.loadError.set(null);
        this.message.set(null);
        this.rowBusy.set({});
        this.statusTarget.set(null);
        this.statusDialog().close();
        this.closeNameDialog();
        this.saving.set(false);
        this.filter.set('active');
      });
    });
    effect(() => {
      this.filter();
      if (this.tenant.tenantId() && this.tenant.isManager()) untracked(() => this.load());
    });
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.tenant.isManager()) return;
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.request = this.api
      .invoke(listTicketCategories, { tenantId, includeInactive: this.filter() === 'all' })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (list) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.items.set(list);
          this.loading.set(false);
          if (this.refocusAfterLoad) {
            this.refocusAfterLoad = false;
            this.heading()?.nativeElement.focus(); // the deactivated row's buttons are gone
          }
        },
        error: (e: unknown) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.refocusAfterLoad = false;
          this.loading.set(false);
          this.loadError.set(this.errorKey(e, 'status'));
        },
      });
  }

  protected setFilter(filter: Filter): void {
    if (this.filter() === filter) return;
    this.message.set(null);
    this.filter.set(filter);
  }

  protected openCreate(): void {
    this.openNameDialog(null);
  }

  protected openRename(category: TicketCategoryDto): void {
    if (this.rowBusy()[category.id]) return;
    this.openNameDialog(category);
  }

  private openNameDialog(target: TicketCategoryDto | null): void {
    if (!this.tenant.isManager()) return;
    this.target.set(target);
    this.name.reset(target?.name ?? '');
    this.dialogError.set(null);
    const el = this.nameDialog().nativeElement;
    if (!el.open) el.showModal();
  }

  protected closeNameDialog(): void {
    const el = this.nameDialog().nativeElement;
    if (el.open) el.close();
  }

  /** Escape must not close the dialog while a request is in flight. */
  protected onCancel(event: Event): void {
    if (this.saving()) event.preventDefault();
  }

  protected submit(): void {
    this.name.markAsTouched();
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.tenant.isManager() || this.name.invalid || this.saving()) return;
    const name = this.name.value.trim();
    const target = this.target();
    if (target && name === target.name) return this.closeNameDialog(); // nothing to change
    const operation: Operation = target ? 'rename' : 'create';
    const call: Observable<TicketCategoryDto> = target
      ? this.api.invoke(renameTicketCategory, { tenantId, categoryId: target.id, body: { name } })
      : this.api.invoke(createTicketCategory, { tenantId, body: { name } });
    this.saving.set(true);
    this.dialogError.set(null);
    this.message.set(null);
    call.pipe(timeout(REQUEST_TIMEOUT_MS)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        if (tenantId !== this.tenant.tenantId()) return;
        this.closeNameDialog();
        this.message.set({
          key: `settings.categories.${operation === 'create' ? 'created' : 'renamed'}`,
          error: false,
          params: { name: saved.name },
        });
        this.load();
      },
      error: (e: unknown) => {
        this.saving.set(false);
        if (tenantId !== this.tenant.tenantId()) return;
        this.dialogError.set(this.errorKey(e, operation)); // the dialog keeps its value
        if (e instanceof HttpErrorResponse && e.status === 404) this.load(); // renamed row no longer exists
      },
    });
  }

  protected askStatus(category: TicketCategoryDto): void {
    if (this.rowBusy()[category.id]) return;
    this.statusTarget.set(category);
    this.statusDialog().open();
  }

  protected confirmStatus(): void {
    const tenantId = this.tenant.tenantId();
    const category = this.statusTarget();
    if (!tenantId || !category || !this.tenant.isManager() || this.rowBusy()[category.id]) return;
    const isActive = !category.isActive;
    this.setBusy(category.id, true);
    this.message.set(null);
    this.api
      .invoke(setTicketCategoryStatus, { tenantId, categoryId: category.id, body: { isActive } })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          this.setBusy(category.id, false);
          if (tenantId !== this.tenant.tenantId()) return;
          this.statusDialog().close();
          this.message.set({
            key: `settings.categories.${isActive ? 'activated' : 'deactivated'}`,
            error: false,
            params: { name: category.name },
          });
          this.refocusAfterLoad = !isActive && this.filter() === 'active';
          this.load();
        },
        error: (e: unknown) => {
          this.setBusy(category.id, false);
          if (tenantId !== this.tenant.tenantId()) return;
          this.statusDialog().close();
          this.message.set({ key: this.errorKey(e, 'status'), error: true });
          if (e instanceof HttpErrorResponse && e.status === 404) this.load();
        },
      });
  }

  private setBusy(id: string, busy: boolean): void {
    this.rowBusy.update((current) => {
      const { [id]: _removed, ...rest } = current;
      return busy ? { ...rest, [id]: true } : rest;
    });
  }

  /** The status decides the message; the duplicate-name 409 only exists for create and rename. */
  private errorKey(e: unknown, operation: Operation | 'status'): string {
    if (e instanceof HttpErrorResponse) {
      if (e.status === 400) return 'settings.categories.errors.invalid';
      if (e.status === 403) return 'common.forbidden';
      if (e.status === 404) return 'settings.categories.errors.notFound';
      if (e.status === 409 && operation !== 'status') return 'settings.categories.errors.duplicate';
    }
    return requestErrorKey(e, 'settings.categories.errors.generic');
  }
}
