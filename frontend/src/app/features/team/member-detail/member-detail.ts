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
import { Subscription } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { GraphqlError } from '../../../core/graphql/graphql-client';
import { pascal } from '../../../shared/format';
import { MemberActions, errorKey } from '../member-actions/member-actions';
import { Skill, TeamApi, TeamMemberDetail } from '../team-api';

const SKILL_NAME_MAX = 50;

/**
 * One employee of the current workspace. Everything shown comes from the server, which also decides what this caller may do
 * (canEditProfileAndSkills, canChangeRoleAndStatus); edits carry the version the editor saw so concurrent changes are not overwritten.
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
  protected readonly skillNameMax = SKILL_NAME_MAX;

  protected readonly detail = signal<TeamMemberDetail | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  protected readonly profile = new FormGroup({
    jobTitle: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    department: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(100)] }),
    phone: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(40)] }),
  });
  protected readonly savingProfile = signal(false);
  protected readonly profileMessage = signal<{ key: string; error: boolean } | null>(null);

  /** Skill catalog of the workspace (active ones can be newly given) and the editor's working selection. */
  protected readonly catalog = signal<Skill[]>([]);
  protected readonly selected = signal<string[]>([]);
  protected readonly newSkill = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(SKILL_NAME_MAX)] });
  protected readonly savingSkills = signal(false);
  protected readonly skillsMessage = signal<{ key: string; error: boolean } | null>(null);

  protected readonly originalSkillIds = computed(() => this.detail()?.member.skills.map((s) => s.id) ?? []);
  protected readonly skillsDirty = computed(() => {
    const a = [...this.selected()].sort();
    const b = [...this.originalSkillIds()].sort();
    return a.length !== b.length || a.some((id, i) => id !== b[i]);
  });
  protected readonly selectedSkills = computed(() => {
    const byId = new Map<string, { id: string; name: string; isActive: boolean }>();
    for (const s of this.detail()?.member.skills ?? []) byId.set(s.id, s);
    for (const s of this.catalog()) byId.set(s.id, s);
    return this.selected()
      .map((id) => byId.get(id))
      .filter((s): s is { id: string; name: string; isActive: boolean } => !!s);
  });
  protected readonly addable = computed(() => this.catalog().filter((s) => s.isActive && !this.selected().includes(s.id)));

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
      untracked(() => this.load());
    });
  }

  protected load(): void {
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(null);
    this.request = this.api.member(this.membershipId()).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.loading.set(false);
        this.profile.reset({ jobTitle: d.member.jobTitle ?? '', department: d.member.department ?? '', phone: d.phone ?? '' });
        this.selected.set(d.member.skills.map((s) => s.id));
        if (d.canEditProfileAndSkills) this.loadCatalog();
      },
      error: (e: unknown) => {
        this.detail.set(null);
        this.loading.set(false);
        this.loadError.set(loadErrorKey(e));
      },
    });
  }

  protected saveProfile(): void {
    const d = this.detail();
    if (!d?.canEditProfileAndSkills || this.profile.invalid || this.savingProfile()) return;
    this.savingProfile.set(true);
    this.profileMessage.set(null);
    this.api.updateProfile(d.member.membershipId, this.profile.getRawValue(), d.profileUpdatedAtUtc).subscribe({
      next: () => {
        this.savingProfile.set(false);
        this.profileMessage.set({ key: 'team.profileSaved', error: false });
        this.load();
      },
      error: (e: unknown) => {
        this.savingProfile.set(false);
        const code = problemCode(e);
        this.profileMessage.set({ key: errorKey(e, code), error: true });
        if (code === 'MEMBER_CHANGED') this.load();
      },
    });
  }

  protected addSkill(id: string): void {
    if (id && !this.selected().includes(id)) this.selected.update((s) => [...s, id]);
  }

  protected removeSkill(id: string): void {
    this.selected.update((s) => s.filter((x) => x !== id));
  }

  /** Creates a workspace skill (or reuses an existing one with the same name) and adds it to the selection. */
  protected createSkill(): void {
    const name = this.newSkill.value.trim();
    if (!name || this.newSkill.invalid || this.savingSkills()) return;
    const existing = this.catalog().find((s) => s.name.toLocaleLowerCase() === name.toLocaleLowerCase());
    if (existing) {
      if (existing.isActive) this.addSkill(existing.id);
      else this.skillsMessage.set({ key: 'team.skillInactive', error: true });
      this.newSkill.reset();
      return;
    }

    this.savingSkills.set(true);
    this.skillsMessage.set(null);
    this.api.createSkill(name).subscribe({
      next: (skill) => {
        this.savingSkills.set(false);
        this.catalog.update((c) => [...c, skill].sort((a, b) => a.name.localeCompare(b.name)));
        this.addSkill(skill.id);
        this.newSkill.reset();
      },
      error: (e: unknown) => {
        this.savingSkills.set(false);
        const duplicate = e instanceof HttpErrorResponse && e.status === 409;
        this.skillsMessage.set({ key: duplicate ? 'team.skillDuplicate' : errorKey(e, problemCode(e)), error: true });
        if (duplicate) this.loadCatalog();
      },
    });
  }

  protected saveSkills(): void {
    const d = this.detail();
    if (!d?.canEditProfileAndSkills || !this.skillsDirty() || this.savingSkills()) return;
    this.savingSkills.set(true);
    this.skillsMessage.set(null);
    this.api.setSkills(d.member.membershipId, this.selected(), this.originalSkillIds()).subscribe({
      next: () => {
        this.savingSkills.set(false);
        this.skillsMessage.set({ key: 'team.skillsSaved', error: false });
        this.load();
      },
      error: (e: unknown) => {
        this.savingSkills.set(false);
        const code = problemCode(e);
        this.skillsMessage.set({ key: errorKey(e, code), error: true });
        if (code === 'MEMBER_CHANGED') this.load();
      },
    });
  }

  protected resetSkills(): void {
    this.selected.set(this.originalSkillIds());
    this.skillsMessage.set(null);
  }

  private loadCatalog(): void {
    this.api.skills(true).subscribe({ next: (s) => this.catalog.set(s), error: () => this.catalog.set([]) });
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
