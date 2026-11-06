import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { TimeoutError, timeout } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  createInvitation,
  listInvitations,
  revokeInvitation,
} from '../../core/api/generated/functions';
import { InvitationDto, TenantRole } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { TeamMembersDocument, TeamMembersQuery } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';

const PAGE_SIZE = 20;
/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;

/**
 * The team directory is visible to staff (read-only for Agents). Invitations are managed by Owners and Managers only; what each role may
 * invite follows the server's rules, which remain the real authorization: the UI only avoids offering actions that would be rejected.
 */
@Component({
  selector: 'app-team',
  imports: [ReactiveFormsModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team.html',
  styleUrl: './team.css',
})
export class Team {
  private readonly gql = inject(GraphqlClient);
  private readonly api = inject(Api);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;

  protected readonly canView = computed(() => this.tenant.isStaff());
  protected readonly canInvite = computed(() => {
    const role = this.tenant.role();
    return role === 'TenantOwner' || role === 'Manager';
  });
  /** Owners may invite any role; Managers only Agents and Customers. */
  protected readonly roles = computed<TenantRole[]>(() =>
    this.tenant.role() === 'TenantOwner'
      ? ['Manager', 'Agent', 'Customer', 'TenantOwner']
      : ['Agent', 'Customer'],
  );

  protected readonly data = signal<TeamMembersQuery['teamMembers'] | null>(null);
  protected readonly error = signal(false);
  protected readonly invitations = signal<InvitationDto[]>([]);
  protected readonly invitationsError = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal<{ key: string; error: boolean } | null>(null);
  protected readonly invite = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email],
    }),
    role: new FormControl<TenantRole>('Agent', { nonNullable: true }),
  });
  private page = 1;

  constructor() {
    // Workspace or role changes reset everything that belonged to the previous one.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      const canView = this.canView();
      const canInvite = this.canInvite();
      untracked(() => {
        this.data.set(null);
        this.error.set(false);
        this.invitations.set([]);
        this.invitationsError.set(false);
        this.message.set(null);
        this.invite.reset({ email: '', role: 'Agent' });
        if (!tenantId || !canView) return;
        this.go(1);
        if (canInvite) this.loadInvitations();
      });
    });
  }

  protected pages = (d: NonNullable<TeamMembersQuery['teamMembers']>) =>
    Math.max(1, Math.ceil(d.totalCount / PAGE_SIZE));

  protected go(page: number): void {
    this.page = page;
    this.load();
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    this.error.set(false);
    this.gql.query(TeamMembersDocument, { page: this.page, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (tenantId === this.tenant.tenantId()) this.data.set(r.teamMembers);
      },
      error: () => {
        if (tenantId === this.tenant.tenantId()) this.error.set(true);
      },
    });
  }

  protected loadInvitations(): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.canInvite()) return;
    this.invitationsError.set(false);
    this.api
      .invoke(listInvitations, { tenantId })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (l) => {
          if (tenantId === this.tenant.tenantId()) this.invitations.set(l);
        },
        error: () => {
          if (tenantId === this.tenant.tenantId()) this.invitationsError.set(true);
        },
      });
  }

  protected send(): void {
    this.invite.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (!this.canInvite() || this.invite.invalid || !tenantId || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.api
      .invoke(createInvitation, { tenantId, body: this.invite.getRawValue() })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          this.busy.set(false);
          if (tenantId !== this.tenant.tenantId()) return;
          this.invite.controls.email.reset();
          this.message.set({ key: 'invitations.sent', error: false });
          this.loadInvitations();
        },
        error: (e: unknown) => {
          this.busy.set(false);
          if (tenantId === this.tenant.tenantId())
            this.message.set({ key: this.errorKey(e), error: true });
        },
      });
  }

  protected revoke(invitationId: string): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || !this.canInvite()) return;
    this.api
      .invoke(revokeInvitation, { tenantId, invitationId })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          if (tenantId !== this.tenant.tenantId()) return;
          this.message.set({ key: 'invitations.revoked', error: false });
          this.loadInvitations();
        },
        error: (e: unknown) => {
          if (tenantId === this.tenant.tenantId())
            this.message.set({ key: this.errorKey(e), error: true });
        },
      });
  }

  private errorKey(e: unknown): string {
    if (e instanceof TimeoutError) return 'invitations.timeout';
    if (e instanceof HttpErrorResponse && e.status === 403) return 'common.forbidden';
    if (e instanceof HttpErrorResponse && e.status === 409) return 'invitations.duplicate';
    return 'invitations.error';
  }
}
