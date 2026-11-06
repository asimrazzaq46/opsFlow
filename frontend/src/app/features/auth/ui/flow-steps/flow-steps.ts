import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';

/** The three stages of account setup. The last one is joining the invited workspace or, without an invitation, setting up a workspace. */
@Component({
  selector: 'app-flow-steps',
  imports: [TranslatePipe],
  templateUrl: './flow-steps.html',
  styleUrl: './flow-steps.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FlowSteps {
  readonly current = input.required<1 | 2 | 3>();
  readonly invited = input(false);
  protected readonly steps = computed(() => [
    'create',
    'verify',
    this.invited() ? 'join' : 'workspace',
  ]);
}
