import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input, output, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { TimeoutError } from 'rxjs';
import { TenantRole } from '../../../core/api/generated/models';
import { ConfirmDialog } from '../../../shared/components/confirm-dialog/confirm-dialog';
import { ManagedMember, TeamApi, restRole } from '../team-api';

/** Staff roles an Owner can give through the role endpoint. */
const STAFF_ROLES: readonly TenantRole[] = ['Agent', 'Manager', 'TenantOwner'];

interface PendingChange {
  role?: TenantRole;
  isActive?: boolean;
}

/** Work reported by the server that blocks or accompanies a change. */
interface OpenWork {
  /** Suspension may proceed once acknowledged; a role change never can (the work must be reassigned first). */
  canAcknowledge: boolean;
  change: PendingChange;
  assignments: number;
  tasks: number;
  tickets: { ticketId: string; displayNumber: string; title: string }[];
}

/**
 * Owner-only role and status controls for one staff member. Nothing is sent before confirmation, the role the Owner saw travels with the
 * request (a concurrent change is refused, not overwritten), and open work is shown before an Agent loses the role or is suspended.
 */
@Component({
  selector: 'app-member-actions',
  imports: [TranslatePipe, ConfirmDialog, RouterLink],
  templateUrl: './member-actions.html',
  styleUrl: './member-actions.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MemberActions {
  readonly member = input.required<ManagedMember>();
  /** Emitted after a change succeeded or the member turned out to be stale; the parent reloads. */
  readonly changed = output<void>();

  private readonly api = inject(TeamApi);
  private readonly dialog = viewChild<ConfirmDialog>('dialog');

  protected readonly staffRoles = STAFF_ROLES;
  protected readonly restRole = restRole;
  protected readonly pending = signal<PendingChange | null>(null);
  protected readonly busy = signal(false);
  protected readonly openWork = signal<OpenWork | null>(null);
  protected readonly message = signal<{ key: string; error: boolean } | null>(null);

  /** Changing the select only proposes the change; the select keeps the server's value until it succeeds. */
  protected proposeRole(event: Event): void {
    const select = event.target as HTMLSelectElement;
    const role = select.value as TenantRole;
    const current = restRole(this.member().role);
    select.value = current;
    if (role === current) return;
    this.pending.set({ role });
    this.dialog()?.open();
  }

  protected proposeStatus(): void {
    this.pending.set({ isActive: !this.member().isActive });
    this.dialog()?.open();
  }

  /** The Owner has seen the affected work and still suspends: it shows as unstaffed until reassigned. */
  protected suspendAnyway(): void {
    const work = this.openWork();
    if (!work?.canAcknowledge) return;
    this.pending.set(work.change);
    this.confirm(true);
  }

  protected confirm(acknowledgeOpenWork = false): void {
    const change = this.pending();
    if (!change || this.busy()) return;
    const member = this.member();
    const request =
      change.role !== undefined
        ? this.api.changeRole(member, change.role)
        : this.api.setStatus(member, !!change.isActive, acknowledgeOpenWork);
    this.busy.set(true);
    this.message.set(null);
    this.openWork.set(null);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.dialog()?.close();
        this.message.set({ key: 'team.changed', error: false });
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.dialog()?.close();
        const problem = e instanceof HttpErrorResponse ? (e.error as Record<string, unknown> | null) : null;
        if (problem?.['code'] === 'MEMBER_HAS_OPEN_WORK') {
          this.openWork.set({
            canAcknowledge: problem['canAcknowledge'] === true,
            change,
            assignments: Number(problem['openAssignmentCount'] ?? 0),
            tasks: Number(problem['openTaskCount'] ?? 0),
            tickets: (problem['tickets'] as OpenWork['tickets'] | undefined) ?? [],
          });
          return;
        }
        this.message.set({ key: errorKey(e, problem?.['code']), error: true });
        if (problem?.['code'] === 'MEMBER_CHANGED') this.changed.emit();
      },
    });
  }

  protected closed(): void {
    if (!this.busy()) this.pending.set(null);
  }
}

export function errorKey(e: unknown, code: unknown): string {
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
      return 'team.errors.conflict'; // e.g. the last active Owner, a duplicate skill name or a concurrent change
    default:
      return 'team.errors.failed';
  }
}
