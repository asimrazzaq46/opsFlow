import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { HealthService } from '../../core/services/health.service';

/** Shows the polled `/api/v1/health` answer: a liveness check proving the API answers, not that every dependency works. */
@Component({
  selector: 'app-health-status',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './health-status.html',
  styleUrl: './health-status.css',
})
export class HealthStatus {
  protected readonly state = inject(HealthService).state;
  protected readonly dot = computed(
    () =>
      ({
        online: 'bg-secondary',
        offline: 'bg-error',
        checking: 'bg-tertiary motion-safe:animate-pulse',
      })[this.state()],
  );
}
