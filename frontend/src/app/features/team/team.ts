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
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, debounceTime, distinctUntilChanged, map } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { TenantRole as GqlTenantRole } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';
import { InviteMemberDialog } from './invitations/invite-member-dialog';
import { TeamInvitations } from './invitations/team-invitations';
import { TeamSkills } from './skills/team-skills';
import { MemberActions } from './member-actions/member-actions';
import { Skill, TeamApi, TeamPage, TeamQuery, TeamRow, TeamSort } from './team-api';

const PAGE_SIZE = 25;
const PAGE_SIZES = [10, 25, 50] as const;
const SKILLS_SHOWN = 3;
export type TeamTab = 'team' | 'invitations' | 'skills';
const FILTER_ROLES: readonly GqlTenantRole[] = ['TENANT_OWNER', 'MANAGER', 'AGENT'];

/**
 * The workspace's employee directory. Search, filters, sorting and paging run on the server, which also decides what each caller may see
 * (Agents get no colleagues' emails and open only their own profile). Role and status changes are Owner-only; invitations (a child
 * component) are managed by Owners and Managers.
 */
@Component({
  selector: 'app-team',
  imports: [ReactiveFormsModule, TranslatePipe, RouterLink, MemberActions, TeamInvitations, InviteMemberDialog, TeamSkills],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team.html',
  styleUrl: './team.css',
})
export class Team {
  private readonly api = inject(TeamApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;
  protected readonly filterRoles = FILTER_ROLES;
  protected readonly skillsShown = SKILLS_SHOWN;
  protected readonly pageSizes = PAGE_SIZES;

  protected readonly canView = computed(() => this.tenant.isStaff());
  protected readonly isAdmin = computed(() => this.tenant.isManager());
  /** Only Owners change roles and statuses (server-enforced). */
  protected readonly canManageMembers = computed(() => this.tenant.isOwner());
  protected readonly canInvite = computed(() => this.tenant.isManager());

  /** The selected tab lives in `?tab=`; an unknown value, or Invitations/Skills for someone who may not see it, falls back to Team. */
  private readonly tabParam = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  protected readonly tab = computed<TeamTab>(() => {
    const requested = this.tabParam().get('tab');
    return (requested === 'invitations' || requested === 'skills') && this.canInvite() ? requested : 'team';
  });
  protected readonly tabs = computed<TeamTab[]>(() => (this.canInvite() ? ['team', 'invitations', 'skills'] : ['team']));
  protected readonly notice = signal<string | null>(null);
  private readonly inviteDialog = viewChild(InviteMemberDialog);
  private readonly invitations = viewChild(TeamInvitations);

  protected readonly query = signal<TeamQuery>(initialQuery());
  protected readonly data = signal<TeamPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly skills = signal<Skill[]>([]);
  protected readonly pages = computed(() => Math.max(1, Math.ceil((this.data()?.totalCount ?? 0) / this.query().pageSize)));
  protected readonly search = new FormControl('', { nonNullable: true });

  private request?: Subscription;

  constructor() {
    // Workspace or role changes reset everything that belonged to the previous one.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      const canView = this.canView();
      untracked(() => {
        this.data.set(null);
        this.error.set(false);
        this.skills.set([]);
        this.search.setValue('', { emitEvent: false });
        this.query.set(initialQuery());
        this.notice.set(null);
        if (!tenantId || !canView) return;
        this.loadSkills();
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

  /** The catalog or an assignment changed on the Skills tab: refresh the (active-only) skill filter and the member badges. */
  protected onSkillsChanged(): void {
    this.loadSkills();
    this.reload();
  }

  protected selectTab(tab: TeamTab): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'team' ? null : tab },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** Arrow keys move between tabs (WAI-ARIA tabs pattern with automatic activation). */
  protected onTabKey(event: KeyboardEvent, index: number): void {
    const tabs = this.tabs();
    const next =
      event.key === 'ArrowRight'
        ? index + 1
        : event.key === 'ArrowLeft'
          ? index - 1
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? tabs.length - 1
              : null;
    if (next === null) return;
    event.preventDefault();
    const target = tabs[(next + tabs.length) % tabs.length];
    this.selectTab(target);
    document.getElementById('team-tab-' + target)?.focus();
  }

  protected openInvite(): void {
    this.notice.set(null);
    this.inviteDialog()?.open();
  }

  /** The server accepted the invitation; the history reloads at once when it is on screen, otherwise it loads when opened. */
  protected onInvited(): void {
    this.notice.set('invitations.sent');
    this.invitations()?.reload();
  }

  protected update(change: Partial<TeamQuery>): void {
    this.query.update((q) => ({ ...q, page: 1, ...change }));
  }

  /** Only the offered sizes are accepted; a new size restarts at page 1 and the server returns that slice. */
  protected onPageSize(value: string): void {
    const size = Number(value);
    if ((PAGE_SIZES as readonly number[]).includes(size)) this.update({ pageSize: size });
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
    this.api.skills(false).subscribe({
      next: (s) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.skills.set(s);
        // A skill that is no longer active cannot filter the directory: drop the stale filter (the query change reloads the list).
        const selected = this.query().skillId;
        if (selected && !s.some((k) => k.id === selected)) this.update({ skillId: null });
      },
      error: () => undefined, // the skill filter is optional; the directory itself reports its own errors
    });
  }
}

function initialQuery(): TeamQuery {
  return { search: '', role: null, isActive: null, skillId: null, sortBy: 'name', descending: false, page: 1, pageSize: PAGE_SIZE };
}
