import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, map } from 'rxjs';
import { TicketPriority, TicketStatus } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { ConfirmDialog } from '../../shared/components/confirm-dialog/confirm-dialog';
import { redirectUnlessManager } from '../../core/auth/manager-access';
import { PRIORITY_BADGE, STATUS_BADGE, httpStatus, isForbidden, isNotFound } from '../tickets/ticket-ui';
import { TicketsApi } from '../tickets/tickets-api';
import { CustomersApi } from './customers-api';
import { PortalUserPicker } from './portal-user-picker/portal-user-picker';

const TICKET_PAGE_SIZE = 10;
type Action = 'deactivate' | 'unlink' | 'link' | 'activate' | 'invite';

/** One customer contact: real contact data, portal-user link state and the tickets the server attributes to it. */
@Component({
  selector: 'app-customer-detail',
  imports: [RouterLink, TranslatePipe, DatePipe, ConfirmDialog, PortalUserPicker],
  templateUrl: './customer-detail.html',
  styleUrl: './customer-detail.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CustomerDetail {
  protected readonly tenant = inject(TenantService);
  private readonly api = inject(CustomersApi);
  private readonly ticketsApi = inject(TicketsApi);
  private readonly route = inject(ActivatedRoute);

  protected readonly id = this.route.snapshot.paramMap.get('id')!;
  protected readonly isOwner = this.tenant.isOwner;
  protected readonly statusBadge = STATUS_BADGE;
  protected readonly priorityBadge = PRIORITY_BADGE;
  private readonly notice = toSignal(this.route.queryParamMap.pipe(map((p) => p.get('notice'))), {
    initialValue: null,
  });

  protected readonly deactivateDialog = viewChild<ConfirmDialog>('deactivateDialog');
  protected readonly unlinkDialog = viewChild<ConfirmDialog>('unlinkDialog');
  protected readonly linkDialog = viewChild<ConfirmDialog>('linkDialog');
  protected readonly linkOpen = signal(false);
  protected readonly linkChoice = signal<string | null>(null);
  protected readonly busy = signal<Action | null>(null);
  private readonly feedback = signal<{ key: string; error: boolean } | null>(null);
  protected readonly ticketPage = signal(1);

  /** Tagged with its workspace so a response for a previous workspace is never shown after a switch. */
  protected readonly customer = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId && this.tenant.isManager() ? { tenantId, id: this.id } : undefined;
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

  protected readonly tickets = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId && this.tenant.isManager()
        ? { tenantId, page: this.ticketPage() }
        : undefined;
    },
    stream: ({ params }) =>
      this.ticketsApi
        .list({
          customerId: this.id,
          sortBy: 'updatedAt',
          descending: true,
          page: params.page,
          pageSize: TICKET_PAGE_SIZE,
        })
        .pipe(map((page) => ({ tenantId: params.tenantId, page }))),
  });
  protected readonly ticketData = computed(() => {
    const r = this.tickets.hasValue() ? this.tickets.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r.page : null;
  });
  protected readonly ticketTotal = computed(() => Number(this.ticketData()?.totalCount ?? 0));
  protected readonly ticketPages = computed(() =>
    Math.max(1, Math.ceil(this.ticketTotal() / TICKET_PAGE_SIZE)),
  );

  /** The one-time notice from a redirect after saving, or the result of the latest action. */
  protected readonly message = computed(() => {
    const f = this.feedback();
    if (f) return f;
    const n = this.notice();
    return n === 'created' || n === 'saved' ? { key: n === 'created' ? 'customers.noticeCreated' : 'customers.noticeSaved', error: false } : null;
  });

  constructor() {
    redirectUnlessManager();
  }

  protected openDeactivate(): void {
    this.deactivateDialog()?.open();
  }

  protected openLink(): void {
    this.linkChoice.set(null);
    this.linkOpen.set(true);
    this.linkDialog()?.open();
  }

  protected openUnlink(): void {
    this.unlinkDialog()?.open();
  }

  protected activate(): void {
    this.run('activate', () => this.api.setActive(this.id, true), 'customers.activated');
  }

  protected deactivate(): void {
    this.run('deactivate', () => this.api.setActive(this.id, false), 'customers.deactivated');
  }

  protected unlink(): void {
    this.run('unlink', () => this.api.unlinkUser(this.id), 'customers.unlinked');
  }

  /** Only the link changes; the name is sent back unchanged. */
  protected link(): void {
    const c = this.loaded();
    const linkedUserId = this.linkChoice();
    if (!c || !linkedUserId) return;
    this.run(
      'link',
      () => this.api.update(c.id, { name: c.name, linkedUserId }),
      'customers.linkedSuccess',
    );
  }

  protected invite(): void {
    const c = this.loaded();
    if (c) this.run('invite', () => this.api.invite(c.email), 'invitations.sent', false);
  }

  protected goToTickets(page: number): void {
    if (page >= 1 && page <= this.ticketPages()) this.ticketPage.set(page);
  }

  protected statusLabel = (s: TicketStatus) => 'tickets.status.' + s;
  protected priorityLabel = (p: TicketPriority) => 'tickets.priority.' + p;

  private run(
    action: Action,
    request: () => Observable<unknown>,
    successKey: string,
    reload = true,
  ): void {
    const tenantId = this.tenant.tenantId();
    if (this.busy() || !tenantId) return;
    this.busy.set(action);
    this.feedback.set(null);
    request().subscribe({
      next: () => {
        this.busy.set(null);
        this.closeDialogs();
        if (tenantId !== this.tenant.tenantId()) return;
        this.feedback.set({ key: successKey, error: false });
        if (reload) this.customer.reload();
      },
      error: (e: unknown) => {
        this.busy.set(null);
        this.closeDialogs();
        this.feedback.set({ key: this.errorKey(e, action), error: true });
      },
    });
  }

  private closeDialogs(): void {
    this.deactivateDialog()?.close();
    this.unlinkDialog()?.close();
    this.linkDialog()?.close();
  }

  private errorKey(e: unknown, action: string): string {
    const status = httpStatus(e);
    if (isForbidden(e)) return 'common.forbidden';
    if (isNotFound(e)) return 'customers.notFound';
    if (action === 'invite') return status === 409 ? 'invitations.duplicate' : 'invitations.error';
    if (action === 'link') {
      return status === 409 ? 'customers.linkConflict' : status === 400 ? 'customers.invalidLink' : 'customers.saveError';
    }
    return status === 409 ? 'customers.linkConflict' : 'customers.statusError';
  }
}
