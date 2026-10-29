import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { HealthService } from '../../core/services/health.service';

@Component({
  selector: 'app-health-status',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span
      role="status"
      class="inline-flex items-center gap-2 rounded-full border border-border bg-card px-3 py-1 text-xs font-medium"
    >
      <span class="size-2 rounded-full" [class]="dot()" aria-hidden="true"></span>
      {{ 'health.' + state() | translate }}
    </span>
  `,
})
export class HealthStatus {
  protected readonly state = inject(HealthService).state;
  protected readonly dot = computed(
    () =>
      ({
        online: 'bg-success',
        offline: 'bg-danger',
        checking: 'bg-warning animate-pulse',
      })[this.state()],
  );
}
