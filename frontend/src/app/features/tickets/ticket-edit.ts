import {
  ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, afterNextRender, computed, inject, input, output, signal, viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { map, startWith } from 'rxjs';
import { TicketDetailDto, TicketPriority, UpdateTicketRequest } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { DESCRIPTION_MAX, TITLE_MAX } from './ticket-create';
import { TICKET_PRIORITIES, fieldErrors, httpStatus } from './ticket-ui';
import { TicketsApi } from './tickets-api';

type Field = 'title' | 'description' | 'priority' | 'categoryId';
interface FormValue { title: string; description: string; priority: TicketPriority; categoryId: string }

const valuesOf = (t: TicketDetailDto): FormValue => ({
  title: t.summary.title,
  description: t.description,
  priority: t.summary.priority,
  categoryId: t.summary.categoryId,
});

/**
 * Only the fields that differ from the version the user started from are sent, together with that version. A concurrent change
 * therefore fails with 409 instead of overwriting someone else's work, and untouched fields are never written back.
 */
export function buildUpdate(base: TicketDetailDto, v: FormValue, staff: boolean): UpdateTicketRequest | null {
  const b = valuesOf(base);
  const title = v.title.trim() !== b.title ? v.title.trim() : null;
  const description = v.description.trim() !== b.description ? v.description.trim() : null;
  const priority = staff && v.priority !== b.priority ? v.priority : null;
  const categoryId = staff && v.categoryId !== b.categoryId ? v.categoryId : null;
  if (title === null && description === null && priority === null && categoryId === null) return null;
  return { title, description, priority, categoryId, version: Number(base.summary.version) };
}

/** Side panel (modal dialog) for the supported ticket edit, including the optimistic-concurrency conflict recovery. */
@Component({
  selector: 'app-ticket-edit',
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './ticket-edit.html',
  styleUrl: './ticket-edit.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'close()' },
})
export class TicketEdit implements OnInit, OnDestroy {
  /** The live server state shown on the page (refreshed by realtime events while the panel is open). */
  readonly ticket = input.required<TicketDetailDto>();
  readonly saved = output<TicketDetailDto>();
  readonly closed = output<void>();
  readonly reloaded = output<TicketDetailDto>();

  protected readonly tenant = inject(TenantService);
  private readonly api = inject(TicketsApi);
  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private readonly returnFocus = document.activeElement as HTMLElement | null;

  protected readonly priorities = TICKET_PRIORITIES;
  protected readonly titleMax = TITLE_MAX;
  protected readonly descriptionMax = DESCRIPTION_MAX;
  protected readonly staff = this.tenant.isStaff();

  /** The version the user's edits are based on; replaced only by an explicit user decision. */
  protected readonly base = signal<TicketDetailDto | null>(null);
  /** Latest server state after a 409 caused by a newer version. */
  protected readonly conflict = signal<TicketDetailDto | null>(null);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(3), Validators.maxLength(TITLE_MAX)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(DESCRIPTION_MAX)] }),
    priority: new FormControl<TicketPriority>('Medium', { nonNullable: true }),
    categoryId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  private readonly value = toSignal(this.form.valueChanges.pipe(startWith(null), map(() => this.form.getRawValue())), { requireSync: true });

  protected readonly categories = rxResource({
    params: () => (this.staff ? this.tenant.tenantId() : null) ?? undefined,
    stream: () => this.api.categories(),
  });

  /** The page received a newer version (realtime) while the user was editing: warn before they hit a conflict. */
  protected readonly staleBase = computed(
    () => !this.conflict() && !!this.base() && Number(this.ticket().summary.version) !== Number(this.base()!.summary.version),
  );
  protected readonly dirty = computed(() => !!this.base() && buildUpdate(this.base()!, this.value(), this.staff) !== null);
  /** Field-by-field comparison of the user's edit with the newer server version. */
  protected readonly diff = computed(() => {
    const server = this.conflict();
    const start = this.base();
    if (!server || !start) return [];
    const b = valuesOf(start);
    const s = valuesOf(server);
    const mine = this.value();
    const fields: Field[] = this.staff ? ['title', 'description', 'priority', 'categoryId'] : ['title', 'description'];
    return fields
      .filter((f) => s[f] !== b[f] || mine[f].trim() !== b[f])
      .map((f) => ({ field: f, theirs: this.display(f, s[f]), mine: this.display(f, mine[f]), changedOnServer: s[f] !== b[f], changedByMe: mine[f].trim() !== b[f] }));
  });

  constructor() {
    afterNextRender(() => this.panel().nativeElement.querySelector<HTMLElement>('input, textarea')?.focus());
  }

  ngOnInit(): void {
    const start = this.ticket();
    this.base.set(start);
    this.form.setValue(valuesOf(start));
    if (!this.staff) {
      this.form.controls.priority.disable();
      this.form.controls.categoryId.disable();
    }
  }

  ngOnDestroy(): void {
    this.returnFocus?.focus?.();
  }

  protected invalid(name: Field): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || c.dirty);
  }

  protected save(): void {
    this.form.markAllAsTouched();
    const start = this.base();
    if (!start || this.form.invalid || this.saving() || this.conflict()) return;
    const body = buildUpdate(start, this.form.getRawValue(), this.staff);
    if (!body) return this.close();
    this.saving.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.serverErrors.set({});
    this.api.update(start.summary.id, body).subscribe({
      next: (t) => {
        this.saving.set(false);
        this.saved.emit(t);
      },
      error: (e: unknown) => {
        const status = httpStatus(e);
        if (status === 409) return this.resolveConflict(start);
        this.saving.set(false);
        this.serverErrors.set(fieldErrors(e));
        this.error.set(status === 403 ? 'tickets.edit.forbidden' : status === 400 ? 'tickets.edit.invalid' : status === 404 ? 'tickets.detail.notFound' : 'tickets.edit.error');
      },
    });
  }

  /** Discards the user's edits and continues from the newest server version. */
  protected useServer(): void {
    const latest = this.conflict();
    if (!latest) return;
    this.base.set(latest);
    this.form.reset(valuesOf(latest));
    this.conflict.set(null);
    this.notice.set('tickets.edit.loadedServer');
  }

  /**
   * Keeps the user's edits on top of the newest version; nothing is sent until they review and save again. Fields the user did not
   * touch take the server value, otherwise the next save would silently revert the other user's change.
   */
  protected keepMine(): void {
    const latest = this.conflict();
    const start = this.base();
    if (!latest || !start) return;
    const before = valuesOf(start);
    const server = valuesOf(latest);
    const mine = this.form.getRawValue();
    this.form.patchValue({
      title: mine.title.trim() === before.title ? server.title : mine.title,
      description: mine.description.trim() === before.description ? server.description : mine.description,
      priority: mine.priority === before.priority ? server.priority : mine.priority,
      categoryId: mine.categoryId === before.categoryId ? server.categoryId : mine.categoryId,
    });
    this.base.set(latest);
    this.conflict.set(null);
    this.notice.set('tickets.edit.keptMine');
  }

  protected close(): void {
    if (!this.saving()) this.closed.emit();
  }

  /** Keeps Tab focus inside the dialog. */
  protected trapFocus(event: KeyboardEvent): void {
    if (event.key !== 'Tab') return;
    const items = [...this.panel().nativeElement.querySelectorAll<HTMLElement>('button, input, select, textarea, [href]')].filter(
      (el) => !el.hasAttribute('disabled') && el.offsetParent !== null,
    );
    if (!items.length) return;
    const first = items[0];
    const last = items[items.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      last.focus();
      event.preventDefault();
    } else if (!event.shiftKey && document.activeElement === last) {
      first.focus();
      event.preventDefault();
    }
  }

  /** The 409 has no distinguishing code, so the server state decides: a newer version is a conflict, otherwise the ticket is no longer editable. */
  private resolveConflict(start: TicketDetailDto): void {
    this.api.get(start.summary.id).subscribe({
      next: (latest) => {
        this.saving.set(false);
        this.reloaded.emit(latest);
        if (latest.summary.status === 'Closed') this.error.set('tickets.edit.closed');
        else if (Number(latest.summary.version) !== Number(start.summary.version)) this.conflict.set(latest);
        else this.error.set('tickets.edit.notEditable');
      },
      error: () => {
        this.saving.set(false);
        this.error.set('tickets.edit.conflictUnknown');
      },
    });
  }

  private display(field: Field, value: string): string {
    if (field === 'categoryId') return this.categories.value()?.find((c) => c.id === value)?.name ?? value;
    return value;
  }
}
