import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, debounceTime, distinctUntilChanged, map, timeout } from 'rxjs';
import {
  InvitationHistoryDto,
  InvitationStatus,
  PagedResultOfInvitationHistoryDto,
  TenantRole,
} from '../../../core/api/generated/models';
import { InvitationsService } from '../../../core/api/generated/services/invitations.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { LanguageService } from '../../../i18n/language.service';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';
import { InvitationOperation, invitationErrorKey, invitationStateChanged } from './invitation-errors';

/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;
const PAGE_SIZES = [10, 25, 50] as const;
const STATUSES: readonly InvitationStatus[] = ['Pending', 'Accepted', 'Revoked', 'Expired'];
const STATUS_ICONS: Record<InvitationStatus, string> = {
  Pending: 'schedule',
  Accepted: 'check_circle',
  Revoked: 'block',
  Expired: 'history',
};

/** Tinted surface with dark text keeps AA contrast in both themes; the icon and label carry the meaning as well as the color. */
const STATUS_CLASSES: Record<InvitationStatus, string> = {
  Pending: 'bg-secondary-container text-on-secondary-container',
  Accepted: 'bg-emerald-100 text-emerald-900 dark:bg-emerald-500/15 dark:text-emerald-200',
  Revoked: 'bg-surface-container-high text-on-surface',
  Expired: 'bg-error-container text-on-error-container',
};

type RowAction = 'resend' | 'revoke';

interface HistoryQuery {
  status: InvitationStatus | null;
  search: string;
  page: number;
  pageSize: number;
}

interface Feedback {
  key: string;
  error: boolean;
  params?: Record<string, string>;
}

/**
 * Complete invitation history of the workspace (pending, accepted, revoked, expired) with server-side status filter, search and paging.
 * Resend and revoke exist only for open invitations the caller may manage (Owners every role, Managers Agent and Customer); the server
 * decides again on each request. After every change the page is reloaded, so the screen always shows what the server holds.
 */
@Component({
  selector: 'app-team-invitations',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team-invitations.html',
  styleUrl: './team-invitations.css',
})
export class TeamInvitations {
  private readonly api = inject(InvitationsService);
  private readonly lang = inject(LanguageService);
  protected readonly tenant = inject(TenantService);

  private readonly dialog = viewChild.required<ConfirmDialog>('dialog');
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  protected readonly statuses = STATUSES;
  protected readonly pageSizes = PAGE_SIZES;
  private readonly dateFormat = computed(
    () => new Intl.DateTimeFormat(this.lang.current(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  protected readonly query = signal<HistoryQuery>(initialQuery());
  protected readonly data = signal<PagedResultOfInvitationHistoryDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal(false);
  protected readonly message = signal<Feedback | null>(null);
  protected readonly current = computed(() => Number(this.data()?.page ?? 1));
  protected readonly total = computed(() => Number(this.data()?.totalCount ?? 0));
  protected readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.query().pageSize)));
  protected readonly search = new FormControl('', { nonNullable: true });
  /** Invitations with a request in flight, so a row can never be submitted twice. */
  protected readonly rowBusy = signal<Record<string, RowAction>>({});
  protected readonly revokeTarget = signal<InvitationHistoryDto | null>(null);
  protected readonly revokeBusy = computed(() => {
    const target = this.revokeTarget();
    return !!target && this.rowBusy()[target.id] === 'revoke';
  });

  private request?: Subscription;
  private refocusAfterLoad = false;

  constructor() {
    // A workspace switch discards everything that belonged to the previous one.
    effect(() => {
      this.tenant.tenantId();
      untracked(() => {
        this.request?.unsubscribe();
        this.data.set(null);
        this.loadError.set(false);
        this.message.set(null);
        this.rowBusy.set({});
        this.revokeTarget.set(null);
        this.search.setValue('', { emitEvent: false });
        this.query.set(initialQuery());
      });
    });
    effect(() => {
      const q = this.query();
      if (this.tenant.tenantId() && this.tenant.isManager()) untracked(() => this.fetch(q));
    });
    this.search.valueChanges
      .pipe(
        debounceTime(350),
        map((v) => v.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((search) => this.update({ search }));
  }

  /** Reloads the current page, e.g. after an invitation was created elsewhere on the page. */
  reload(): void {
    this.fetch(this.query());
  }

  /** Null for a missing or invalid timestamp, so rendering never throws and the actions stay visible. */
  protected format(iso: string | null | undefined): string | null {
    const time = iso ? Date.parse(iso) : Number.NaN;
    return Number.isNaN(time) ? null : this.dateFormat().format(time);
  }

  /** Only open invitations (pending or expired) can change, and only by someone the server matrix allows. */
  protected canAct(i: InvitationHistoryDto): boolean {
    return (i.status === 'Pending' || i.status === 'Expired') && this.canManage(i.role);
  }

  /** Mirrors the server matrix for the UI only; the server re-checks it on every request. */
  private canManage(role: TenantRole): boolean {
    return this.tenant.isOwner() || (this.tenant.isManager() && (role === 'Agent' || role === 'Customer'));
  }

  protected statusIcon(status: InvitationStatus): string {
    return STATUS_ICONS[status];
  }

  protected statusClass(status: InvitationStatus): string {
    return STATUS_CLASSES[status];
  }

  protected update(change: Partial<HistoryQuery>): void {
    this.query.update((q) => ({ ...q, page: 1, ...change }));
  }

  protected go(page: number): void {
    this.query.update((q) => ({ ...q, page }));
  }

  protected onStatus(value: string): void {
    this.update({ status: STATUSES.find((s) => s === value) ?? null });
  }

  protected onPageSize(value: string): void {
    const size = Number(value);
    if ((PAGE_SIZES as readonly number[]).includes(size)) this.update({ pageSize: size });
  }

  protected load(): void {
    this.fetch(this.query());
  }

  private fetch(q: HistoryQuery): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.tenant.isManager()) return;
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(false);
    this.request = this.api
      .listInvitationHistory({
        tenantId,
        page: q.page,
        pageSize: q.pageSize,
        ...(q.status ? { status: q.status } : {}),
        ...(q.search ? { search: q.search } : {}),
      })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (page) => {
          if (tenantId !== this.tenant.tenantId()) return;
          // The last row of the last page was removed by a filter or another admin: move to the new last page.
          const total = Number(page.totalCount);
          if (!page.items.length && total > 0 && q.page > 1) {
            this.go(Math.ceil(total / q.pageSize));
            return;
          }
          this.data.set(page);
          this.loading.set(false);
          if (this.refocusAfterLoad) {
            this.refocusAfterLoad = false;
            this.heading().nativeElement.focus(); // the revoked row's buttons are gone
          }
        },
        error: () => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.loadError.set(true);
          this.loading.set(false);
        },
      });
  }

  protected resend(invitation: InvitationHistoryDto): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.canAct(invitation) || this.rowBusy()[invitation.id]) return;
    this.setBusy(invitation.id, 'resend');
    this.message.set(null);
    this.api
      .resendInvitation({ tenantId, invitationId: invitation.id })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (updated) => {
          this.setBusy(invitation.id, null);
          if (tenantId !== this.tenant.tenantId()) return;
          // The response carries the new sent and expiry times; show them now, the reload below confirms them.
          if (updated?.id) {
            this.data.update((d) =>
              d
                ? {
                    ...d,
                    items: d.items.map((x) =>
                      x.id === updated.id
                        ? { ...x, status: 'Pending', sentAtUtc: updated.sentAtUtc, expiresAtUtc: updated.expiresAtUtc }
                        : x,
                    ),
                  }
                : d,
            );
          }
          this.message.set({ key: 'invitations.resent', error: false, params: { email: invitation.email } });
          this.load();
        },
        error: (e: unknown) => {
          this.setBusy(invitation.id, null);
          if (tenantId === this.tenant.tenantId()) this.fail(e, 'resend');
        },
      });
  }

  protected askRevoke(invitation: InvitationHistoryDto): void {
    if (!this.canAct(invitation) || this.rowBusy()[invitation.id]) return;
    this.revokeTarget.set(invitation);
    this.dialog().open();
  }

  protected confirmRevoke(): void {
    const invitation = this.revokeTarget();
    const tenantId = this.tenant.tenantId();
    if (!invitation || !tenantId || this.rowBusy()[invitation.id]) return;
    this.setBusy(invitation.id, 'revoke');
    this.message.set(null);
    this.api
      .revokeInvitation({ tenantId, invitationId: invitation.id })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          this.finishRevoke(invitation.id);
          if (tenantId !== this.tenant.tenantId()) return;
          this.message.set({ key: 'invitations.revoked', error: false, params: { email: invitation.email } });
          this.refocusAfterLoad = true;
          this.load(); // the row stays in the history with its new status; an active status filter drops it from the page
        },
        error: (e: unknown) => {
          this.finishRevoke(invitation.id);
          if (tenantId === this.tenant.tenantId()) this.fail(e, 'revoke');
        },
      });
  }

  protected revokeClosed(): void {
    if (!this.revokeBusy()) this.revokeTarget.set(null);
  }

  private finishRevoke(id: string): void {
    this.setBusy(id, null);
    this.revokeTarget.set(null);
    this.dialog().close();
  }

  private setBusy(id: string, action: RowAction | null): void {
    this.rowBusy.update((busy) => {
      const { [id]: _previous, ...rest } = busy;
      return action ? { ...rest, [id]: action } : rest;
    });
  }

  private fail(e: unknown, operation: InvitationOperation): void {
    this.message.set({ key: invitationErrorKey(e, operation), error: true });
    if (invitationStateChanged(e)) this.load();
  }
}

function initialQuery(): HistoryQuery {
  return { status: null, search: '', page: 1, pageSize: 25 };
}
