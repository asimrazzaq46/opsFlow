import { HttpErrorResponse } from '@angular/common/http';
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
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, TimeoutError, timeout } from 'rxjs';
import { InvitationDto, TenantRole } from '../../../core/api/generated/models';
import { InvitationsService } from '../../../core/api/generated/services/invitations.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { LanguageService } from '../../../i18n/language.service';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';

/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;

type RowAction = 'resend' | 'revoke';
type Operation = 'create' | RowAction;

interface Feedback {
  key: string;
  error: boolean;
  params?: Record<string, string>;
}

/**
 * Pending and expired invitations of the workspace with create, resend and revoke. Every action is a real call; the list is reloaded
 * afterwards and after a conflict, so the screen always shows what the server holds. Owners manage every role, Managers only Agent and
 * Customer invitations; the server decides again on each request.
 */
@Component({
  selector: 'app-team-invitations',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog],
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

  /** Owners may invite any role; Managers only Agents and Customers. */
  protected readonly roles = computed<TenantRole[]>(() =>
    this.tenant.isOwner() ? ['Manager', 'Agent', 'Customer', 'TenantOwner'] : ['Agent', 'Customer'],
  );
  private readonly dateFormat = computed(
    () => new Intl.DateTimeFormat(this.lang.current(), { dateStyle: 'medium', timeStyle: 'short' }),
  );

  protected readonly invitations = signal<InvitationDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal(false);
  protected readonly message = signal<Feedback | null>(null);
  protected readonly creating = signal(false);
  /** Invitations with a request in flight, so a row can never be submitted twice. */
  protected readonly rowBusy = signal<Record<string, RowAction>>({});
  protected readonly revokeTarget = signal<InvitationDto | null>(null);
  protected readonly revokeBusy = computed(() => {
    const target = this.revokeTarget();
    return !!target && this.rowBusy()[target.id] === 'revoke';
  });
  protected readonly invite = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    role: new FormControl<TenantRole>('Agent', { nonNullable: true }),
  });

  private request?: Subscription;
  private refocusAfterLoad = false;

  constructor() {
    // A workspace switch discards everything that belonged to the previous one.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      untracked(() => {
        this.request?.unsubscribe();
        this.invitations.set([]);
        this.loadError.set(false);
        this.message.set(null);
        this.rowBusy.set({});
        this.revokeTarget.set(null);
        this.invite.reset({ email: '', role: 'Agent' });
        if (tenantId) this.load();
      });
    });
  }

  protected format(iso: string): string {
    return this.dateFormat().format(new Date(iso));
  }

  protected isExpired(i: InvitationDto): boolean {
    return Date.parse(i.expiresAtUtc) <= Date.now();
  }

  /** Mirrors the server matrix for the UI only; the server re-checks it on every request. */
  protected canManage(role: TenantRole): boolean {
    return this.tenant.isOwner() || (this.tenant.isManager() && (role === 'Agent' || role === 'Customer'));
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.tenant.isManager()) return;
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(false);
    this.request = this.api
      .listInvitations({ tenantId, includeExpired: true })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (list) => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.invitations.set(list);
          this.loading.set(false);
          if (this.refocusAfterLoad) {
            this.refocusAfterLoad = false;
            this.heading().nativeElement.focus(); // the revoked row (and its button) is gone
          }
        },
        error: () => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.loadError.set(true);
          this.loading.set(false);
        },
      });
  }

  protected send(): void {
    this.invite.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (!this.tenant.isManager() || this.invite.invalid || !tenantId || this.creating()) return;
    this.creating.set(true);
    this.message.set(null);
    this.api
      .createInvitation({ tenantId, body: this.invite.getRawValue() })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          this.creating.set(false);
          if (tenantId !== this.tenant.tenantId()) return;
          this.invite.controls.email.reset();
          this.message.set({ key: 'invitations.sent', error: false });
          this.load();
        },
        error: (e: unknown) => {
          this.creating.set(false);
          if (tenantId === this.tenant.tenantId()) this.fail(e, 'create'); // the form keeps its values
        },
      });
  }

  protected resend(invitation: InvitationDto): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.canManage(invitation.role) || this.rowBusy()[invitation.id]) return;
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
          if (updated?.id) this.invitations.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
          this.message.set({ key: 'invitations.resent', error: false, params: { email: invitation.email } });
          this.load();
        },
        error: (e: unknown) => {
          this.setBusy(invitation.id, null);
          if (tenantId === this.tenant.tenantId()) this.fail(e, 'resend');
        },
      });
  }

  protected askRevoke(invitation: InvitationDto): void {
    if (!this.canManage(invitation.role) || this.rowBusy()[invitation.id]) return;
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
          this.load();
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

  /** The server code and status decide the message; after a conflict or a vanished invitation the list is refreshed. */
  private fail(e: unknown, operation: Operation): void {
    this.message.set({ key: this.failureKey(e, operation), error: true });
    const stale =
      e instanceof HttpErrorResponse &&
      (e.status === 404 || (e.status === 409 && problemCode(e) !== 'INVITATION_ALREADY_MEMBER'));
    if (stale) this.load();
  }

  private failureKey(e: unknown, operation: Operation): string {
    if (e instanceof TimeoutError) return 'invitations.timeout';
    if (!(e instanceof HttpErrorResponse)) return 'invitations.error';
    const code = problemCode(e);
    switch (e.status) {
      case 400:
        return 'invitations.errors.invalid';
      case 403:
        return 'common.forbidden';
      case 404:
        return 'invitations.errors.notFound';
      case 409:
        if (code === 'INVITATION_ALREADY_MEMBER') return 'invitations.errors.alreadyMember';
        if (code === 'INVITATION_NOT_RESENDABLE') return 'invitations.errors.notResendable';
        // Duplicate-pending, accepted and concurrent changes carry no stable code on these operations.
        return `invitations.errors.${operation}Conflict`;
      case 429:
        return code === 'INVITATION_RESEND_COOLDOWN'
          ? 'invitations.errors.cooldown'
          : 'invitations.errors.rateLimited';
      default:
        return 'invitations.error';
    }
  }
}

function problemCode(e: HttpErrorResponse): string | undefined {
  const code = (e.error as { code?: unknown } | null)?.code;
  return typeof code === 'string' ? code : undefined;
}
