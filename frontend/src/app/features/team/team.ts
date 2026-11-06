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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, TimeoutError, debounceTime, distinctUntilChanged, map, timeout } from 'rxjs';
import { InvitationsService } from '../../core/api/generated/services/invitations.service';
import { InvitationDto, TenantRole } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { TenantRole as GqlTenantRole } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';
import { MemberActions } from './member-actions/member-actions';
import { Skill, TeamApi, TeamPage, TeamQuery, TeamRow, TeamSort } from './team-api';

const PAGE_SIZE = 20;
const SKILLS_SHOWN = 3;
/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;
const FILTER_ROLES: readonly GqlTenantRole[] = ['TENANT_OWNER', 'MANAGER', 'AGENT'];

/**
 * The workspace's employee directory. Search, filters, sorting and paging run on the server, which also decides what each caller may see
 * (Agents get no colleagues' emails and open only their own profile). Role and status changes are Owner-only; invitations are managed by
 * Owners and Managers.
 */
@Component({
  selector: 'app-team',
  imports: [ReactiveFormsModule, TranslatePipe, RouterLink, MemberActions],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team.html',
  styleUrl: './team.css',
})
export class Team {
  private readonly api = inject(TeamApi);
  private readonly invitationsService = inject(InvitationsService);
  private readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;
  protected readonly filterRoles = FILTER_ROLES;
  protected readonly skillsShown = SKILLS_SHOWN;

  protected readonly canView = computed(() => this.tenant.isStaff());
  protected readonly isAdmin = computed(() => this.tenant.isManager());
  /** Only Owners change roles and statuses (server-enforced). */
  protected readonly canManageMembers = computed(() => this.tenant.isOwner());
  protected readonly canInvite = computed(() => this.tenant.isManager());
  /** Owners may invite any role; Managers only Agents and Customers. */
  protected readonly roles = computed<TenantRole[]>(() =>
    this.tenant.isOwner() ? ['Manager', 'Agent', 'Customer', 'TenantOwner'] : ['Agent', 'Customer'],
  );

  protected readonly query = signal<TeamQuery>(initialQuery());
  protected readonly data = signal<TeamPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly skills = signal<Skill[]>([]);
  protected readonly pages = computed(() => Math.max(1, Math.ceil((this.data()?.totalCount ?? 0) / PAGE_SIZE)));
  protected readonly search = new FormControl('', { nonNullable: true });

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
  private request?: Subscription;

  constructor() {
    // Workspace or role changes reset everything that belonged to the previous one.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      const canView = this.canView();
      const canInvite = this.canInvite();
      untracked(() => {
        this.data.set(null);
        this.error.set(false);
        this.skills.set([]);
        this.invitations.set([]);
        this.invitationsError.set(false);
        this.message.set(null);
        this.invite.reset({ email: '', role: 'Agent' });
        this.search.setValue('', { emitEvent: false });
        this.query.set(initialQuery());
        if (!tenantId || !canView) return;
        this.loadSkills();
        if (canInvite) this.loadInvitations();
      });
    });
    effect(() => {
      const q = this.query();
      if (this.tenant.tenantId() && this.canView()) untracked(() => this.load(q));
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

  protected update(change: Partial<TeamQuery>): void {
    this.query.update((q) => ({ ...q, page: 1, ...change }));
  }

  protected go(page: number): void {
    this.query.update((q) => ({ ...q, page }));
  }

  protected sort(by: TeamSort): void {
    const q = this.query();
    this.update({ sortBy: by, descending: q.sortBy === by ? !q.descending : false });
  }

  protected ariaSort(by: TeamSort): 'ascending' | 'descending' | 'none' {
    const q = this.query();
    return q.sortBy !== by ? 'none' : q.descending ? 'descending' : 'ascending';
  }

  protected onRole(value: string): void {
    this.update({ role: (value || null) as GqlTenantRole | null });
  }

  protected onStatus(value: string): void {
    this.update({ isActive: value === '' ? null : value === 'active' });
  }

  protected onSkill(value: string): void {
    this.update({ skillId: value || null });
  }

  /** Administrators open every profile; other staff only their own (the server enforces the same rule). */
  protected canOpen(row: TeamRow): boolean {
    return this.isAdmin() || row.userId === this.auth.user()?.id;
  }

  protected reload(): void {
    this.load(this.query());
  }

  private load(q: TeamQuery): void {
    const tenantId = this.tenant.tenantId();
    this.request?.unsubscribe();
    this.loading.set(true);
    this.error.set(false);
    this.request = this.api.directory(q).subscribe({
      next: (page) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.data.set(page);
        this.loading.set(false);
      },
      error: () => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  private loadSkills(): void {
    const tenantId = this.tenant.tenantId();
    this.api.skills(true).subscribe({
      next: (s) => {
        if (tenantId === this.tenant.tenantId()) this.skills.set(s);
      },
      error: () => undefined, // the skill filter is optional; the directory itself reports its own errors
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

  private errorKey(e: unknown): string {
    if (e instanceof TimeoutError) return 'invitations.timeout';
    if (e instanceof HttpErrorResponse && e.status === 403) return 'common.forbidden';
    if (e instanceof HttpErrorResponse && e.status === 409) return 'invitations.duplicate';
    return 'invitations.error';
  }
}

function initialQuery(): TeamQuery {
  return { search: '', role: null, isActive: null, skillId: null, sortBy: 'name', descending: false, page: 1, pageSize: PAGE_SIZE };
}
