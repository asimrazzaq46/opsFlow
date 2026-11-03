import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { debounceTime, merge } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { DashboardDocument, DashboardQuery } from '../../core/graphql/generated';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { BarChart } from '../../shared/bar-chart';
import { TrendChart } from '../../shared/trend-chart';
import { defaultRange, minutesLabel, pascal } from '../../shared/format';

type Dash = DashboardQuery['dashboard'];

@Component({
  selector: 'app-dashboard',
  imports: [ReactiveFormsModule, TranslatePipe, BarChart, TrendChart, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard {
  private readonly gql = inject(GraphqlClient);
  private readonly i18n = inject(TranslateService);
  protected readonly tenant = inject(TenantService);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly priorities = ['Low', 'Medium', 'High', 'Urgent'];
  protected readonly data = signal<Dash | null>(null);
  protected readonly error = signal(false);
  protected readonly forbidden = signal(false);
  private readonly range = defaultRange(30);
  protected readonly form = new FormGroup({
    from: new FormControl(this.range.from, { nonNullable: true }),
    to: new FormControl(this.range.to, { nonNullable: true }),
    priority: new FormControl('', { nonNullable: true }),
  });
  private seq = 0;

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) untracked(() => this.load());
      else this.data.set(null); // never keep another workspace's numbers
    });
    // the existing notification push (ticket created/assigned/status) and reconnects trigger ONE debounced reconcile
    merge(this.realtime.on('notification.created'), this.realtime.reconnected)
      .pipe(debounceTime(1500), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
  }

  protected load(): void {
    const { from, to, priority } = this.form.getRawValue();
    const id = ++this.seq;
    this.error.set(false);
    this.gql.query(DashboardDocument, { filter: { from, to, priority: priority ? (priority.toUpperCase() as 'LOW') : null } }).subscribe({
      next: (r) => {
        if (id === this.seq) this.data.set(r.dashboard); // drop stale responses
      },
      error: (e: { code?: string }) => {
        if (id !== this.seq) return;
        this.forbidden.set(e?.code === 'FORBIDDEN');
        this.error.set(true);
      },
    });
  }

  protected currentCards(d: Dash) {
    const staff = d.sla !== null;
    return [
      { key: 'open', value: d.current.openTickets, link: '/tickets', params: {} },
      ...(staff
        ? [
            { key: 'unassigned', value: d.current.unassignedTickets, link: '/kanban', params: {} },
            { key: 'overdue', value: d.current.overdueTickets, link: '/kanban', params: { overdue: 1 } },
          ]
        : []),
      { key: 'resolvedToday', value: d.current.resolvedToday, link: '/tickets', params: {} },
    ];
  }

  protected firstResponse(d: Dash): string | null {
    return minutesLabel(d.avgFirstResponseMinutes);
  }

  protected trendData(d: Dash) {
    return d.trend.map((p) => ({ label: p.periodStartUtc.slice(0, 10), created: p.created, resolved: p.resolved }));
  }

  protected statusData = (d: Dash) => d.openByStatus.map((s) => ({ label: this.i18n.instant('tickets.status.' + s.key), value: s.count }));
  protected priorityData = (d: Dash) => d.byPriority.map((s) => ({ label: this.i18n.instant('tickets.priority.' + pascal(s.key)), value: s.count }));
  protected workloadData = (w: NonNullable<Dash['workload']>) => w.map((x) => ({ label: x.displayName, value: x.activeTickets }));
}
