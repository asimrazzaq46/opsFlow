import { HttpErrorResponse } from '@angular/common/http';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, debounceTime, distinctUntilChanged, map } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';
import { pascal } from '../../../shared/format';
import { errorKey } from '../member-actions/member-actions';
import { SkillUsage, TeamApi, TeamPage, TeamQuery, TeamRow } from '../team-api';

const SKILL_NAME_MAX = 50;
const MEMBER_PAGE_SIZE = 10;

interface Feedback {
  key: string;
  error: boolean;
  params?: Record<string, string | number>;
}

/**
 * Workspace skill catalog (create, rename, activate, deactivate) and bulk assignment of ONE skill to several members. Catalog changes
 * apply to the whole workspace and never add or remove a skill from an individual member; assignments change only the chosen members and
 * leave their other skills alone. The server checks every request again (Owners any staff member, Managers only Agents).
 */
@Component({
  selector: 'app-team-skills',
  imports: [ReactiveFormsModule, TranslatePipe, ConfirmDialog, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './team-skills.html',
  styleUrl: './team-skills.css',
})
export class TeamSkills {
  /** Emitted after a catalog or assignment change succeeded, so the directory refreshes its skill filter and badges. */
  readonly changed = output<void>();

  private readonly api = inject(TeamApi);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;
  protected readonly skillNameMax = SKILL_NAME_MAX;

  private readonly deactivateDialog = viewChild.required<ConfirmDialog>('deactivateDialog');
  private readonly assignDialog = viewChild<ElementRef<HTMLDialogElement>>('assignDialog');

  // ---- catalog
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly skills = signal<SkillUsage[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadError = signal(false);
  protected readonly message = signal<Feedback | null>(null);
  protected readonly newSkill = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(SKILL_NAME_MAX)] });
  protected readonly renameName = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(SKILL_NAME_MAX)] });
  protected readonly editingId = signal<string | null>(null);
  /** The skill with a request in flight; one catalog request at a time. */
  protected readonly busyId = signal<string | null>(null);
  protected readonly creating = signal(false);
  protected readonly deactivateTarget = signal<SkillUsage | null>(null);

  // ---- bulk assignment
  protected readonly assignTarget = signal<SkillUsage | null>(null);
  protected readonly memberSearch = new FormControl('', { nonNullable: true });
  protected readonly memberQuery = signal<TeamQuery>(this.initialMemberQuery());
  protected readonly members = signal<TeamPage | null>(null);
  protected readonly membersLoading = signal(false);
  protected readonly membersError = signal(false);
  /** Memberships ticked by the user; kept across pages and searches until an assignment succeeds. */
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly assignBusy = signal<'add' | 'remove' | null>(null);
  protected readonly assignMessage = signal<Feedback | null>(null);
  protected readonly currentMemberPage = computed(() => Number(this.members()?.page ?? 1));
  protected readonly memberPages = computed(() =>
    Math.max(1, Math.ceil(Number(this.members()?.totalCount ?? 0) / this.memberQuery().pageSize)),
  );

  private request?: Subscription;
  private memberRequest?: Subscription;

  constructor() {
    // A workspace switch discards everything that belonged to the previous one.
    effect(() => {
      const tenantId = this.tenant.tenantId();
      untracked(() => {
        this.request?.unsubscribe();
        this.memberRequest?.unsubscribe();
        this.skills.set(null);
        this.message.set(null);
        this.closeAssign();
        this.search.setValue('', { emitEvent: false });
        if (tenantId && this.tenant.isManager()) this.loadCatalog();
      });
    });
    this.search.valueChanges
      .pipe(debounceTime(350), map((v) => v.trim()), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe(() => this.loadCatalog());
    this.memberSearch.valueChanges
      .pipe(debounceTime(350), map((v) => v.trim()), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe((search) => {
        this.memberQuery.update((q) => ({ ...q, search, page: 1 }));
        this.loadMembers();
      });
  }

  // ------------------------------------------------------------------ catalog

  protected loadCatalog(): void {
    const tenantId = this.tenant.tenantId();
    this.request?.unsubscribe();
    this.loading.set(true);
    this.loadError.set(false);
    this.request = this.api.skillUsage(this.search.value.trim()).subscribe({
      next: (list) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.skills.set(list);
        this.loading.set(false);
      },
      error: () => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.loadError.set(true);
        this.loading.set(false);
      },
    });
  }

  protected createSkill(): void {
    const name = this.newSkill.value.trim();
    if (!name || this.newSkill.invalid || this.creating()) return;
    if (this.nameTaken(name)) {
      this.message.set({ key: 'team.skillDuplicate', error: true });
      return;
    }
    this.creating.set(true);
    this.message.set(null);
    this.api.createSkill(name).subscribe({
      next: () => {
        this.creating.set(false);
        this.newSkill.reset();
        this.message.set({ key: 'team.skillCreated', error: false });
        this.loadCatalog();
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.creating.set(false);
        this.fail(e);
      },
    });
  }

  protected startRename(skill: SkillUsage): void {
    if (this.busyId()) return;
    this.editingId.set(skill.id);
    this.renameName.setValue(skill.name);
    this.message.set(null);
  }

  protected cancelRename(): void {
    this.editingId.set(null);
  }

  protected saveRename(skill: SkillUsage): void {
    const name = this.renameName.value.trim();
    if (this.busyId()) return;
    if (!name || this.renameName.invalid) {
      this.message.set({ key: 'team.errors.invalid', error: true });
      return;
    }
    if (name === skill.name) return this.cancelRename();
    if (this.nameTaken(name, skill.id)) {
      this.message.set({ key: 'team.skillDuplicate', error: true });
      return;
    }
    this.update(skill, { name }, 'team.skillRenamed');
  }

  protected askDeactivate(skill: SkillUsage): void {
    if (this.busyId()) return;
    this.deactivateTarget.set(skill);
    this.deactivateDialog().open();
  }

  protected confirmDeactivate(): void {
    const skill = this.deactivateTarget();
    if (skill) this.update(skill, { isActive: false }, 'team.skillDeactivated');
  }

  protected activate(skill: SkillUsage): void {
    if (!this.busyId()) this.update(skill, { isActive: true }, 'team.skillActivated');
  }

  protected deactivateClosed(): void {
    if (!this.busyId()) this.deactivateTarget.set(null);
  }

  /** Members keep a deactivated skill, so only the catalog entry changes; holder counts are reloaded from the server. */
  private update(skill: SkillUsage, change: { name?: string; isActive?: boolean }, successKey: string): void {
    this.busyId.set(skill.id);
    this.message.set(null);
    this.api.updateSkill(skill, change).subscribe({
      next: () => {
        this.busyId.set(null);
        this.deactivateTarget.set(null);
        this.deactivateDialog().close();
        this.editingId.set(null);
        this.message.set({ key: successKey, error: false });
        this.loadCatalog();
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.busyId.set(null);
        this.deactivateTarget.set(null);
        this.deactivateDialog().close();
        this.fail(e);
      },
    });
  }

  private nameTaken(name: string, exceptId?: string): boolean {
    const wanted = name.toLocaleLowerCase();
    return (this.skills() ?? []).some((s) => s.id !== exceptId && s.name.toLocaleLowerCase() === wanted);
  }

  private fail(e: unknown): void {
    const status = e instanceof HttpErrorResponse ? e.status : 0;
    this.message.set({
      key: status === 409 ? 'team.skillDuplicate' : status === 404 ? 'team.skillGone' : errorKey(e, undefined),
      error: true,
    });
    if (status === 409 || status === 404) this.loadCatalog();
  }

  // ------------------------------------------------------------------ bulk assignment

  /** Owners can pick every staff member; Managers only Agents (the server refuses anything else). */
  private initialMemberQuery(): TeamQuery {
    return {
      search: '',
      role: this.tenant.isOwner() ? null : 'AGENT',
      isActive: null,
      skillId: null,
      sortBy: 'name',
      descending: false,
      page: 1,
      pageSize: MEMBER_PAGE_SIZE,
    };
  }

  protected openAssign(skill: SkillUsage): void {
    if (!this.tenant.isManager()) return;
    this.assignTarget.set(skill);
    this.selected.set(new Set());
    this.assignMessage.set(null);
    this.memberSearch.setValue('', { emitEvent: false });
    this.memberQuery.set(this.initialMemberQuery());
    this.loadMembers();
    const el = this.assignDialog()?.nativeElement;
    if (el && !el.open) el.showModal();
  }

  protected closeAssign(): void {
    this.memberRequest?.unsubscribe();
    const el = this.assignDialog()?.nativeElement;
    if (el?.open) el.close();
    this.assignTarget.set(null);
  }

  /** Escape must not close the dialog while a request is in flight. */
  protected onAssignCancel(event: Event): void {
    if (this.assignBusy()) event.preventDefault();
  }

  protected goMembers(page: number): void {
    this.memberQuery.update((q) => ({ ...q, page }));
    this.loadMembers();
  }

  private loadMembers(): void {
    const tenantId = this.tenant.tenantId();
    this.memberRequest?.unsubscribe();
    this.membersLoading.set(true);
    this.membersError.set(false);
    this.memberRequest = this.api.directory(this.memberQuery()).subscribe({
      next: (page) => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.members.set(page);
        this.membersLoading.set(false);
      },
      error: () => {
        if (tenantId !== this.tenant.tenantId()) return;
        this.membersError.set(true);
        this.membersLoading.set(false);
      },
    });
  }

  protected hasSkill(member: TeamRow): boolean {
    const target = this.assignTarget();
    return !!target && member.skills.some((s) => s.id === target.id);
  }

  protected toggleMember(id: string): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  /** Sends the whole selection; the server skips members that already match, so a stale tick is harmless. */
  protected submitAssign(mode: 'add' | 'remove'): void {
    const target = this.assignTarget();
    const ids = [...this.selected()];
    if (!target || !ids.length || this.assignBusy() || !this.tenant.isManager()) return;
    if (mode === 'add' && !target.isActive) return;
    this.assignBusy.set(mode);
    this.assignMessage.set(null);
    this.api.assignSkill(target.id, mode === 'add' ? ids : [], mode === 'remove' ? ids : []).subscribe({
      next: (result) => {
        this.assignBusy.set(null);
        this.selected.set(new Set());
        this.assignMessage.set({
          key: 'team.assignDone',
          error: false,
          params: { added: Number(result.addedCount), removed: Number(result.removedCount) },
        });
        this.loadMembers();
        this.loadCatalog();
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.assignBusy.set(null);
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        this.assignMessage.set({ key: status === 404 ? 'team.assignGone' : errorKey(e, undefined), error: true });
        if (status === 404 || status === 409) {
          this.loadMembers();
          this.loadCatalog();
        }
      },
    });
  }
}
