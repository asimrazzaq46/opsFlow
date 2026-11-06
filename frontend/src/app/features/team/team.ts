import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { TimeoutError, timeout } from 'rxjs';
import { InvitationsService } from '../../core/api/generated/services/invitations.service';
import { TenantsService } from '../../core/api/generated/services/tenants.service';
import { InvitationDto, TenantRole } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { TeamMembersDocument, TeamMembersQuery } from '../../core/graphql/generated';
import { ConfirmDialog } from '../../shared/components/confirm-dialog/confirm-dialog';
import { pascal } from '../../shared/format';

type MemberRow = TeamMembersQuery['teamMembers']['items'][number];

/** Staff roles an Owner can give through the existing role endpoint. */
const STAFF_ROLES: readonly TenantRole[] = ['Agent', 'Manager', 'TenantOwner'];

/** A role or status change waiting for the Owner's confirmation. */
interface PendingChange {
  member: MemberRow;
  role?: TenantRole;
  isActive?: boolean;
}

/** Work that blocks moving an Agent to another role, as reported by the server. */
interface OpenWork {
  member: string;
  assignments: number;
  tasks: number;
  tickets: { ticketId: string; displayNumber: string; title: string }[];
}

const PAGE_SIZE = 20;
/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;

/**
 * The team directory is visible to staff (read-only for Agents). Invitations are managed by Owners and Managers only; what each role may
 * invite follows the server's rules, which remain the real authorization: the UI only avoids offering actions that would be rejected.
 */
@Component({
  selector: 'app-team',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team.html',
  styleUrl: './team.css',
})
export class Team {
  private readonly gql = inject(GraphqlClient);
  private readonly invitationsService = inject(InvitationsService);
  private readonly tenantsService = inject(TenantsService);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;
  protected readonly staffRoles = STAFF_ROLES;
  private readonly changeDialog = viewChild<ConfirmDialog>('changeDialog');

  /** Only Owners change roles and statuses (server-enforced); other staff get a read-only list. */
  protected readonly canManageMembers = computed(() => this.tenant.role() === 'TenantOwner');
  protected readonly pending = signal<PendingChange | null>(null);
  protected readonly changing = signal(false);
  protected readonly openWork = signal<OpenWork | null>(null);
  protected readonly changeMessage = signal<{ key: string; error: boolean } | null>(null);

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
        this.openWork.set(null);
        this.pending.set(null);
        this.changeMessage.set(null);
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
    this.invitationsService
      .listInvitations({ tenantId })
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
    this.invitationsService
      .createInvitation({ tenantId, body: this.invite.getRawValue() })
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
    this.invitationsService
      .revokeInvitation({ tenantId, invitationId })
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

  protected roleOf(m: MemberRow): TenantRole {
    return pascal(m.role) as TenantRole;
  }

  /** Changing the role select only proposes the change; nothing is sent before confirmation. */
  protected proposeRole(m: MemberRow, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const role = select.value as TenantRole;
    select.value = this.roleOf(m); // the list keeps showing the server's value until the change succeeds
    if (role === this.roleOf(m)) return;
    this.pending.set({ member: m, role });
    this.changeDialog()?.open();
  }

  protected proposeStatus(m: MemberRow): void {
    this.pending.set({ member: m, isActive: !m.isActive });
    this.changeDialog()?.open();
  }

  protected confirmChange(): void {
    const change = this.pending();
    const tenantId = this.tenant.tenantId();
    if (!change || !tenantId || this.changing()) return;
    const membershipId = change.member.id;
    // The role the Owner saw: a concurrent change by another administrator is refused instead of overwritten.
    const expectedRole = this.roleOf(change.member);
    const request =
      change.role !== undefined
        ? this.tenantsService.changeMemberRole({ tenantId, membershipId, body: { role: change.role, expectedRole } })
        : this.tenantsService.setMemberStatus({ tenantId, membershipId, body: { isActive: !!change.isActive, expectedRole } });
    this.changing.set(true);
    this.changeMessage.set(null);
    this.openWork.set(null);
    request.pipe(timeout(REQUEST_TIMEOUT_MS)).subscribe({
      next: () => {
        this.changing.set(false);
        this.changeDialog()?.close();
        if (tenantId !== this.tenant.tenantId()) return;
        this.changeMessage.set({ key: 'team.changed', error: false });
        this.load();
      },
      error: (e: unknown) => {
        this.changing.set(false);
        this.changeDialog()?.close();
        if (tenantId !== this.tenant.tenantId()) return;
        const problem = e instanceof HttpErrorResponse ? (e.error as Record<string, unknown> | null) : null;
        if (problem?.['code'] === 'MEMBER_HAS_OPEN_WORK') {
          this.openWork.set({
            member: change.member.displayName,
            assignments: Number(problem['openAssignmentCount'] ?? 0),
            tasks: Number(problem['openTaskCount'] ?? 0),
            tickets: (problem['tickets'] as OpenWork['tickets'] | undefined) ?? [],
          });
          return;
        }
        this.changeMessage.set({ key: this.changeErrorKey(e, problem?.['code']), error: true });
        if (problem?.['code'] === 'MEMBER_CHANGED') this.load();
      },
    });
  }

  private changeErrorKey(e: unknown, code: unknown): string {
    if (e instanceof TimeoutError) return 'invitations.timeout';
    if (!(e instanceof HttpErrorResponse)) return 'team.errors.failed';
    if (code === 'MEMBER_CHANGED') return 'team.errors.stale';
    switch (e.status) {
      case 400:
        return 'team.errors.invalid';
      case 403:
        return 'common.forbidden';
      case 404:
        return 'team.errors.notFound';
      case 409:
        return 'team.errors.conflict'; // e.g. the last active Owner, or a concurrent change
      default:
        return 'team.errors.failed';
    }
  }

  private errorKey(e: unknown): string {
    if (e instanceof TimeoutError) return 'invitations.timeout';
    if (e instanceof HttpErrorResponse && e.status === 403) return 'common.forbidden';
    if (e instanceof HttpErrorResponse && e.status === 409) return 'invitations.duplicate';
    return 'invitations.error';
  }
}
