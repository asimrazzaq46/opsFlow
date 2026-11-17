import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Observable, Subscription, catchError, map, of, switchMap } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { GraphqlError } from '../../../core/graphql/graphql-client';
import { pascal } from '../../../shared/format';
import { MemberActions, errorKey } from '../member-actions/member-actions';
import { Skill, TeamApi, TeamMemberDetail } from '../team-api';

/** Which unsaved edits must survive a reload of the server data. */
interface KeepEdits {
  profile: boolean;
  skills: boolean;
}

/**
 * One employee of the current workspace. Everything shown comes from the server, which also decides what this caller may do
 * (canEditProfileAndSkills, canChangeRoleAndStatus); edits carry the version the editor saw so concurrent changes are not overwritten.
 * Skills here are this member's assignments only; the workspace skill catalog is administered on the Skills tab of the Team page.
 */
@Component({
  selector: 'app-member-detail',
  imports: [ReactiveFormsModule, TranslatePipe, RouterLink, DatePipe, MemberActions],
  templateUrl: './member-detail.html',
  styleUrl: './member-detail.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MemberDetail {
  readonly membershipId = input.required<string>();

  private readonly api = inject(TeamApi);
  private readonly router = inject(Router);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;

  protected readonly detail = signal<TeamMemberDetail | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  /** Read-only by default; Edit unlocks only what the server allows this caller to change. */
  protected readonly editing = signal(false);
  protected readonly saving = signal(false);
  protected readonly notice = signal<string | null>(null);

  protected readonly profile = new FormGroup({
    jobTitle: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    department: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    phone: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(40)] }),
  });
  protected readonly profileMessage = signal<{ key: string; error: boolean } | null>(null);

  /** Active workspace skills that can be given to this member, and the editor's working selection (local until Save). */
  protected readonly catalog = signal<Skill[]>([]);
  protected readonly selected = signal<string[]>([]);
  protected readonly skillQuery = signal('');
  protected readonly skillsMessage = signal<{ key: string; error: boolean } | null>(null);

  protected readonly originalSkillIds = computed(() => this.detail()?.member.skills.map((s) => s.id) ?? []);
  protected readonly skillsDirty = computed(() => {
    const a = [...this.selected()].sort();
    const b = [...this.originalSkillIds()].sort();
    return a.length !== b.length || a.some((id, i) => id !== b[i]);
  });
  /** The working set (the saved one in read-only mode), resolved to catalog entries; includes preserved inactive assignments. */
  private readonly workingSkills = computed(() => {
    const byId = new Map<string, { id: string; name: string; isActive: boolean }>();
    for (const s of this.detail()?.member.skills ?? []) byId.set(s.id, s);
    for (const s of this.catalog()) byId.set(s.id, s);
    const ids = this.editing() ? this.selected() : this.originalSkillIds();
    return ids.map((id) => byId.get(id)).filter((s): s is { id: string; name: string; isActive: boolean } => !!s);
  });
  /** Only active catalog skills count as the member's skills. */
  protected readonly activeSkills = computed(() => this.workingSkills().filter((s) => s.isActive));
  /** Deactivated skills stay stored on the member; only an editor sees them, apart, and removal is an explicit action. */
  protected readonly inactiveSkills = computed(() => (this.editing() ? this.workingSkills().filter((s) => !s.isActive) : []));
  protected readonly pickable = computed(() => {
    const term = this.skillQuery().trim().toLocaleLowerCase();
    return this.catalog().filter((s) => s.isActive && (!term || s.name.toLocaleLowerCase().includes(term)));
  });

  private openedIn = this.tenant.tenantId();
  private request?: Subscription;

  constructor() {
    effect(() => {
      // A member belongs to ONE workspace: switching workspaces leaves the page instead of showing another tenant's data.
      if (this.tenant.tenantId() !== this.openedIn) void this.router.navigateByUrl('/team');
    });
    effect(() => {
      this.membershipId();
      this.tenant.role(); // a changed role (membership.changed) changes what this caller may see or do
      untracked(() => this.reloadKeepingEdits());
    });
  }

  /** Refreshes the server data without discarding what the user is typing or selecting. */
  protected reloadKeepingEdits(): void {
    this.load({ profile: this.editing() && !this.profile.pristine, skills: this.editing() && this.skillsDirty() });
  }

  protected load(keep: KeepEdits = { profile: false, skills: false }): void {
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.request = this.api.member(this.membershipId()).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.loading.set(false);
        if (!keep.profile) {
          this.profile.reset({ jobTitle: d.member.jobTitle ?? '', department: d.member.department ?? '', phone: d.phone ?? '' });
        }
        if (!keep.skills) this.selected.set(d.member.skills.map((s) => s.id));
        if (d.canEditProfileAndSkills) this.loadCatalog();
      },
      error: (e: unknown) => {
        this.detail.set(null);
        this.loading.set(false);
        this.loadError.set(loadErrorKey(e));
      },
    });
  }

  protected startEdit(): void {
    this.notice.set(null);
    this.profileMessage.set(null);
    this.skillsMessage.set(null);
    this.editing.set(true);
  }

  /** Discards every unsaved change (profile fields and skill selection) and returns to the read-only view. */
  protected cancelEdit(): void {
    if (this.saving()) return;
    const d = this.detail();
    if (d) this.profile.reset({ jobTitle: d.member.jobTitle ?? '', department: d.member.department ?? '', phone: d.phone ?? '' });
    this.selected.set(this.originalSkillIds());
    this.skillQuery.set('');
    this.profileMessage.set(null);
    this.skillsMessage.set(null);
    this.editing.set(false);
  }

  /**
   * Profile and skills are two independent server operations, sent one after the other; each reports its own outcome, so a failure of one
   * never implies the other was undone. A part that failed keeps its unsaved edits so nothing the user entered is lost.
   * Role and status changes have their own confirmation and are not part of this save.
   */
  protected save(): void {
    const d = this.detail();
    if (!d?.canEditProfileAndSkills || this.saving() || this.profile.invalid) return;
    const profileDirty = !this.profile.pristine;
    const skillsDirty = this.skillsDirty();
    if (!profileDirty && !skillsDirty) return this.cancelEdit();
    const attempt = (op: Observable<unknown> | null): Observable<unknown> =>
      op ? op.pipe(map(() => null), catchError((e: unknown) => of(e))) : of(null);
    this.saving.set(true);
    this.profileMessage.set(null);
    this.skillsMessage.set(null);
    this.notice.set(null);
    attempt(profileDirty ? this.api.updateProfile(d.member.membershipId, this.profile.getRawValue(), d.profileUpdatedAtUtc) : null)
      .pipe(
        switchMap((profileError) =>
          attempt(skillsDirty ? this.api.setSkills(d.member.membershipId, this.selected(), this.originalSkillIds()) : null).pipe(
            map((skillsError) => ({ profileError, skillsError })),
          ),
        ),
      )
      .subscribe(({ profileError, skillsError }) => {
        this.saving.set(false);
        if (profileError) this.profileMessage.set({ key: errorKey(profileError, problemCode(profileError)), error: true });
        if (skillsError) this.skillsMessage.set({ key: errorKey(skillsError, problemCode(skillsError)), error: true });
        if (!profileError && !skillsError) {
          this.editing.set(false);
          this.notice.set('team.saved');
          this.load();
          return;
        }
        // A stale member must be reloaded as the server has it; otherwise only the saved part is refreshed and the failed part is kept.
        const stale = problemCode(profileError) === 'MEMBER_CHANGED' || problemCode(skillsError) === 'MEMBER_CHANGED';
        this.load(stale ? { profile: false, skills: false } : { profile: !!profileError, skills: !!skillsError });
      });
  }

  protected addSkill(id: string): void {
    if (!this.selected().includes(id)) this.selected.update((s) => [...s, id]);
  }

  protected removeSkill(id: string): void {
    this.selected.update((s) => s.filter((x) => x !== id));
  }

  protected toggleSkill(id: string): void {
    if (this.selected().includes(id)) this.removeSkill(id);
    else this.addSkill(id);
  }

  private loadCatalog(): void {
    this.api.skills(false).subscribe({ next: (s) => this.catalog.set(s), error: () => this.catalog.set([]) });
  }
}

function problemCode(e: unknown): unknown {
  return e instanceof HttpErrorResponse ? (e.error as Record<string, unknown> | null)?.['code'] : undefined;
}

/** GraphQL reports refusals as errors with codes; anything else is a load failure the user can retry. */
function loadErrorKey(e: unknown): string {
  const code = e instanceof GraphqlError ? e.errors[0]?.extensions?.['code'] : undefined;
  if (code === 'FORBIDDEN' || code === 'AUTH_NOT_AUTHORIZED') return 'team.detailForbidden';
  if (code === 'NOT_FOUND') return 'team.errors.notFound';
  return 'team.detailError';
}
