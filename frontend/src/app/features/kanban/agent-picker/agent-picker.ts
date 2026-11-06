import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { initials } from '../../workspaces/workspace-api';

export interface PickerAgent {
  id: string;
  userId: string;
  displayName: string;
  email: string;
}

export interface PickerAssignee {
  membershipId: string;
  displayName: string;
}

/**
 * Compact assignee control of a board card. It only reports which Agent was chosen: the server adds the assignment (self-claim or a
 * teammate), validates that the Agent is an active Agent of this workspace and rejects duplicates, and the card is reloaded afterwards.
 * Nothing is removed from here.
 */
@Component({
  selector: 'app-agent-picker',
  imports: [TranslatePipe],
  templateUrl: './agent-picker.html',
  styleUrl: './agent-picker.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgentPicker {
  /** Used to give the popup a unique id for aria-controls. */
  readonly ticketKey = input.required<string>();
  readonly assignees = input.required<readonly PickerAssignee[]>();
  /** Active Agents of the CURRENT workspace only; the parent clears it when the workspace changes. */
  readonly agents = input.required<readonly PickerAgent[]>();
  readonly loading = input(false);
  readonly loadFailed = input(false);
  /** The caller's own membership id in this workspace, when known. */
  readonly me = input<string | null>(null);
  /** The option whose request is in flight; only that option is disabled. */
  readonly busyId = input<string | null>(null);
  readonly readOnly = input(false);

  readonly pick = output<string>();
  readonly retry = output<void>();

  protected readonly initials = initials;
  protected readonly open = signal(false);
  protected readonly query = signal('');
  private readonly search = viewChild<ElementRef<HTMLInputElement>>('search');

  protected readonly popupId = computed(() => 'assign-' + this.ticketKey());
  protected readonly assignedIds = computed(() => new Set(this.assignees().map((a) => a.membershipId)));
  protected readonly visible = computed(() => this.assignees().slice(0, 3));
  protected readonly extra = computed(() => Math.max(0, this.assignees().length - 3));
  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    const agents = this.agents();
    return q
      ? agents.filter((a) => a.displayName.toLowerCase().includes(q) || a.email.toLowerCase().includes(q))
      : agents;
  });
  /** Duplicate display names are told apart by e-mail. */
  protected readonly ambiguous = computed(() => {
    const seen = new Map<string, number>();
    for (const a of this.agents()) seen.set(a.displayName, (seen.get(a.displayName) ?? 0) + 1);
    return new Set([...seen].filter(([, n]) => n > 1).map(([name]) => name));
  });

  protected toggle(): void {
    this.open.update((v) => !v);
    if (this.open()) setTimeout(() => this.search()?.nativeElement.focus());
  }

  protected close(): void {
    this.open.set(false);
    this.query.set('');
  }

  protected choose(agent: PickerAgent): void {
    if (this.busyId() !== null || this.assignedIds().has(agent.id)) return;
    this.pick.emit(agent.id);
  }

  protected onSearch(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
