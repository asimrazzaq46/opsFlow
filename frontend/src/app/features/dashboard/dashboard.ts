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
  template: `
    <div class="flex flex-wrap items-end justify-between gap-4">
      <h1 class="text-2xl font-bold tracking-tight">{{ 'dashboard.title' | translate }}</h1>
      <form [formGroup]="form" class="flex flex-wrap items-end gap-3 text-sm" (ngSubmit)="load()">
        <label class="grid gap-1"><span class="text-muted">{{ 'filters.from' | translate }}</span>
          <input type="date" formControlName="from" class="rounded-lg border border-border bg-card px-2 py-1" /></label>
        <label class="grid gap-1"><span class="text-muted">{{ 'filters.to' | translate }}</span>
          <input type="date" formControlName="to" class="rounded-lg border border-border bg-card px-2 py-1" /></label>
        <label class="grid gap-1"><span class="text-muted">{{ 'filters.priority' | translate }}</span>
          <select formControlName="priority" class="rounded-lg border border-border bg-card px-2 py-1">
            <option value="">{{ 'filters.any' | translate }}</option>
            @for (p of priorities; track p) { <option [value]="p">{{ 'tickets.priority.' + p | translate }}</option> }
          </select></label>
        <button type="submit" class="rounded-lg bg-brand px-3 py-1.5 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">{{ 'filters.apply' | translate }}</button>
      </form>
    </div>

    <div class="mt-6" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">{{ (forbidden() ? 'common.forbidden' : 'dashboard.loadError') | translate }}
          <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (data(); as d) {
        <p class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">{{ 'dashboard.currentState' | translate }}</p>
        <section class="grid grid-cols-2 gap-3 lg:grid-cols-4">
          @for (k of currentCards(d); track k.key) {
            <a [routerLink]="k.link" [queryParams]="k.params" class="rounded-xl border border-border bg-card p-4 hover:border-brand focus-visible:outline-2 focus-visible:outline-brand">
              <p class="text-sm text-muted">{{ 'dashboard.kpi.' + k.key | translate }}</p>
              <p class="mt-1 text-3xl font-bold tabular-nums">{{ k.value }}</p>
            </a>
          }
        </section>

        @if (d.sla; as sla) {
          <p class="mb-2 mt-8 text-xs font-semibold uppercase tracking-wide text-muted">{{ 'dashboard.period' | translate }} · {{ d.timeZone }}</p>
          <section class="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <div class="rounded-xl border border-border bg-card p-4">
              <p class="text-sm text-muted">{{ 'dashboard.kpi.avgFirstResponse' | translate }}</p>
              <p class="mt-1 text-3xl font-bold">{{ firstResponse(d) ?? ('common.na' | translate) }}</p>
              <p class="text-xs text-muted">{{ 'dashboard.sample' | translate: { n: d.firstResponseSample } }}</p>
            </div>
            <div class="rounded-xl border border-border bg-card p-4">
              <p class="text-sm text-muted">{{ 'dashboard.kpi.slaCompliance' | translate }}</p>
              <p class="mt-1 text-3xl font-bold">{{ sla.compliancePercent === null ? ('common.na' | translate) : sla.compliancePercent + '%' }}</p>
              <p class="text-xs text-muted">{{ 'dashboard.slaDetail' | translate: { met: sla.met, breached: sla.breached } }}</p>
            </div>
            <div class="rounded-xl border border-border bg-card p-4">
              <p class="text-sm text-muted">{{ 'dashboard.kpi.created' | translate }}</p>
              <p class="mt-1 text-3xl font-bold tabular-nums">{{ d.createdInPeriod }}</p>
            </div>
            <div class="rounded-xl border border-border bg-card p-4">
              <p class="text-sm text-muted">{{ 'dashboard.kpi.resolved' | translate }}</p>
              <p class="mt-1 text-3xl font-bold tabular-nums">{{ d.resolvedInPeriod }}</p>
            </div>
          </section>
        }

        <section class="mt-8 grid gap-4 lg:grid-cols-2">
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'dashboard.charts.trend' | translate }}</h2>
            @if (d.trend.length) {
              <app-trend-chart [data]="trendData(d)" [title]="'dashboard.charts.trend' | translate" [createdLabel]="'dashboard.kpi.created' | translate" [resolvedLabel]="'dashboard.kpi.resolved' | translate" />
            } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'dashboard.charts.status' | translate }}</h2>
            @if (d.openByStatus.length) {
              <app-bar-chart [data]="statusData(d)" [title]="'dashboard.charts.status' | translate" />
            } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'dashboard.charts.priority' | translate }}</h2>
            @if (d.byPriority.length) {
              <app-bar-chart [data]="priorityData(d)" [title]="'dashboard.charts.priority' | translate" />
            } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          @if (d.workload; as workload) {
            <article class="rounded-xl border border-border bg-card p-4">
              <h2 class="mb-3 font-semibold">{{ 'dashboard.charts.workload' | translate }}</h2>
              @if (workload.length) {
                <app-bar-chart [data]="workloadData(workload)" [title]="'dashboard.charts.workload' | translate" />
              } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
            </article>
          }
        </section>
      } @else {
        <p class="text-muted">{{ 'common.loading' | translate }}</p>
      }
    </div>
  `,
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
