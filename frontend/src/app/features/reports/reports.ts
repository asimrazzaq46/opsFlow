import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe, TranslateService } from '@ngx-translate/core';
import { Api } from '../../core/api/generated/api';
import { exportReportCsv } from '../../core/api/generated/functions';
import { ReportExportKind } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { ReportDocument, ReportQuery } from '../../core/graphql/generated';
import { BarChart } from '../../shared/bar-chart';
import { TrendChart } from '../../shared/trend-chart';
import { defaultRange, minutesLabel, pascal } from '../../shared/format';

type Rep = ReportQuery['report'];

@Component({
  selector: 'app-reports',
  imports: [ReactiveFormsModule, TranslatePipe, BarChart, TrendChart],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-end justify-between gap-4">
      <h1 class="text-2xl font-bold tracking-tight">{{ 'reports.title' | translate }}</h1>
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

    <div class="mt-4 flex flex-wrap items-center gap-2 text-sm">
      <span class="text-muted">{{ 'reports.export' | translate }}:</span>
      @for (k of kinds; track k) {
        <button type="button" class="rounded-lg border border-border px-3 py-1 hover:border-brand focus-visible:outline-2 focus-visible:outline-brand disabled:opacity-50"
          [disabled]="exporting()" (click)="download(k)">{{ 'reports.kind.' + k | translate }}</button>
      }
      @if (notice(); as n) { <span role="status" class="text-warning">{{ n | translate }}</span> }
    </div>

    <div class="mt-6" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">{{ (forbidden() ? 'common.forbidden' : 'reports.loadError') | translate }}
          <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (data(); as r) {
        <p class="mb-2 text-xs text-muted">{{ 'reports.timezone' | translate: { tz: r.timeZone } }}</p>
        <section class="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <div class="rounded-xl border border-border bg-card p-4"><p class="text-sm text-muted">{{ 'reports.created' | translate }}</p><p class="mt-1 text-3xl font-bold tabular-nums">{{ r.ticketsCreated }}</p></div>
          <div class="rounded-xl border border-border bg-card p-4"><p class="text-sm text-muted">{{ 'reports.avgResolution' | translate }}</p>
            <p class="mt-1 text-3xl font-bold">{{ minutes(r.resolution.avgMinutes) ?? ('common.na' | translate) }}</p>
            <p class="text-xs text-muted">{{ 'reports.median' | translate }}: {{ minutes(r.resolution.medianMinutes) ?? ('common.na' | translate) }} · {{ 'reports.cycles' | translate: { n: r.resolution.completedCycles } }}</p></div>
          <div class="rounded-xl border border-border bg-card p-4"><p class="text-sm text-muted">{{ 'dashboard.kpi.slaCompliance' | translate }}</p>
            <p class="mt-1 text-3xl font-bold">{{ r.sla.compliancePercent === null ? ('common.na' | translate) : r.sla.compliancePercent + '%' }}</p>
            <p class="text-xs text-muted">{{ 'dashboard.slaDetail' | translate: { met: r.sla.met, breached: r.sla.breached } }}</p>
            <p class="text-xs text-muted">{{ 'reports.openOverdue' | translate: { n: r.openOverdueTickets } }}</p></div>
          <div class="rounded-xl border border-border bg-card p-4"><p class="text-sm text-muted">{{ 'reports.reopenRate' | translate }}</p>
            <p class="mt-1 text-3xl font-bold">{{ r.reopen.reopenRatePercent === null ? ('common.na' | translate) : r.reopen.reopenRatePercent + '%' }}</p>
            <p class="text-xs text-muted">{{ 'reports.reopenDetail' | translate: { a: r.reopen.reopenedCycles, b: r.reopen.resolvedCycles, c: r.reopen.reopenEventsInPeriod } }}</p></div>
        </section>

        <section class="mt-8 grid gap-4 lg:grid-cols-2">
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'dashboard.charts.trend' | translate }}</h2>
            @if (r.trend.length) {
              <app-trend-chart [data]="trend(r)" [title]="'dashboard.charts.trend' | translate" [createdLabel]="'dashboard.kpi.created' | translate" [resolvedLabel]="'dashboard.kpi.resolved' | translate" />
            } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'reports.byStatus' | translate }}</h2>
            @if (r.byStatus.length) { <app-bar-chart [data]="status(r)" [title]="'reports.byStatus' | translate" /> } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'reports.categories' | translate }}</h2>
            @if (r.categories.length) { <app-bar-chart [data]="categories(r)" [title]="'reports.categories' | translate" /> } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
          <article class="rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'reports.byPriority' | translate }}</h2>
            @if (r.byPriority.length) { <app-bar-chart [data]="priority(r)" [title]="'reports.byPriority' | translate" /> } @else { <p class="text-sm text-muted">{{ 'common.noData' | translate }}</p> }
          </article>
        </section>

        <section class="mt-8 grid gap-4 lg:grid-cols-2">
          <article class="overflow-x-auto rounded-xl border border-border bg-card p-4">
            <h2 class="mb-1 font-semibold">{{ 'reports.agents' | translate }}</h2>
            <p class="mb-3 text-xs text-muted">{{ 'reports.agentsNote' | translate }}</p>
            <table class="w-full text-left text-sm">
              <thead class="text-muted"><tr><th class="py-1">{{ 'team.name' | translate }}</th><th>{{ 'reports.workload' | translate }}</th><th>{{ 'reports.resolvedBy' | translate }}</th></tr></thead>
              <tbody>
                @for (a of r.agents; track a.membershipId) { <tr class="border-t border-border"><td class="py-1">{{ a.displayName }}</td><td>{{ a.currentWorkload }}</td><td>{{ a.resolvedInPeriod }}</td></tr>
                } @empty { <tr><td colspan="3" class="py-2 text-muted">{{ 'common.noData' | translate }}</td></tr> }
              </tbody>
            </table>
          </article>
          <article class="overflow-x-auto rounded-xl border border-border bg-card p-4">
            <h2 class="mb-3 font-semibold">{{ 'reports.customers' | translate }}</h2>
            <table class="w-full text-left text-sm">
              <thead class="text-muted"><tr><th class="py-1">{{ 'customers.name' | translate }}</th><th>{{ 'reports.tickets' | translate }}</th><th>{{ 'reports.open' | translate }}</th><th>{{ 'reports.resolved' | translate }}</th><th>{{ 'reports.overdue' | translate }}</th></tr></thead>
              <tbody>
                @for (c of r.customers; track c.customerId) { <tr class="border-t border-border"><td class="py-1">{{ c.name }}</td><td>{{ c.tickets }}</td><td>{{ c.open }}</td><td>{{ c.resolved }}</td><td>{{ c.overdueOpen }}</td></tr>
                } @empty { <tr><td colspan="5" class="py-2 text-muted">{{ 'common.noData' | translate }}</td></tr> }
              </tbody>
            </table>
          </article>
        </section>
      } @else {
        <p class="text-muted">{{ 'common.loading' | translate }}</p>
      }
    </div>
  `,
})
export class Reports {
  private readonly gql = inject(GraphqlClient);
  private readonly api = inject(Api);
  private readonly i18n = inject(TranslateService);
  protected readonly tenant = inject(TenantService);

  protected readonly priorities = ['Low', 'Medium', 'High', 'Urgent'];
  protected readonly kinds: ReportExportKind[] = ['Tickets', 'Agents', 'Categories', 'Customers'];
  protected readonly data = signal<Rep | null>(null);
  protected readonly error = signal(false);
  protected readonly forbidden = signal(false);
  protected readonly exporting = signal(false);
  protected readonly notice = signal<string | null>(null);
  private readonly range = defaultRange(30);
  protected readonly form = new FormGroup({
    from: new FormControl(this.range.from, { nonNullable: true }),
    to: new FormControl(this.range.to, { nonNullable: true }),
    priority: new FormControl('', { nonNullable: true }),
  });
  protected readonly minutes = minutesLabel;
  private seq = 0;

  constructor() {
    effect(() => {
      if (this.tenant.tenantId()) untracked(() => this.load());
      else this.data.set(null);
    });
  }

  protected load(): void {
    const { from, to, priority } = this.form.getRawValue();
    const id = ++this.seq;
    this.error.set(false);
    this.gql.query(ReportDocument, { filter: { from, to, priority: priority ? (priority.toUpperCase() as 'LOW') : null } }).subscribe({
      next: (r) => {
        if (id === this.seq) this.data.set(r.report);
      },
      error: (e: { code?: string }) => {
        this.forbidden.set(e?.code === 'FORBIDDEN');
        this.error.set(true);
      },
    });
  }

  /** Same filters as the screen; the server caps ticket exports and reports it in X-Export-Truncated. */
  protected download(kind: ReportExportKind): void {
    const tenantId = this.tenant.tenantId();
    if (!tenantId || this.exporting()) return;
    const { from, to, priority } = this.form.getRawValue();
    this.exporting.set(true);
    this.notice.set(null);
    this.api.invoke$Response(exportReportCsv, { tenantId, kind, from, to, priority: priority || undefined }).subscribe({
      next: (res) => {
        const blob = new Blob(['﻿', String(res.body ?? '')], { type: 'text/csv;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${kind.toLowerCase()}-${from}-${to}.csv`;
        a.click();
        URL.revokeObjectURL(url);
        if (res.headers.get('X-Export-Truncated') === 'true') this.notice.set('reports.truncated');
        this.exporting.set(false);
      },
      error: (e: unknown) => {
        this.notice.set(e instanceof HttpErrorResponse && e.status === 403 ? 'common.forbidden' : 'reports.exportError');
        this.exporting.set(false);
      },
    });
  }

  protected trend = (r: Rep) => r.trend.map((p) => ({ label: p.periodStartUtc.slice(0, 10), created: p.created, resolved: p.resolved }));
  protected status = (r: Rep) => r.byStatus.map((s) => ({ label: this.i18n.instant('tickets.status.' + s.key), value: s.count }));
  protected priority = (r: Rep) => r.byPriority.map((s) => ({ label: this.i18n.instant('tickets.priority.' + s.key), value: s.count }));
  protected categories = (r: Rep) => r.categories.map((c) => ({ label: `${c.name} (${c.sharePercent}%)`, value: c.count }));
  protected readonly pascal = pascal;
}
