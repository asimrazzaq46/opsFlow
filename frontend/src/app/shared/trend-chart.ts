import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export interface TrendDatum {
  label: string;
  created: number;
  resolved: number;
}

/** Created vs resolved lines in plain SVG (no chart dependency) with a screen-reader table. */
@Component({
  selector: 'app-trend-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 300 100" class="h-40 w-full" role="img" [attr.aria-label]="title()" preserveAspectRatio="none">
      <polyline fill="none" stroke="var(--color-brand)" stroke-width="2" vector-effect="non-scaling-stroke" [attr.points]="created()" />
      <polyline fill="none" stroke="var(--color-success)" stroke-width="2" vector-effect="non-scaling-stroke" [attr.points]="resolved()" />
    </svg>
    <p class="mt-1 flex gap-4 text-xs text-muted">
      <span><span class="text-brand" aria-hidden="true">●</span> {{ createdLabel() }}</span>
      <span><span class="text-success" aria-hidden="true">●</span> {{ resolvedLabel() }}</span>
    </p>
    <table class="sr-only">
      <caption>{{ title() }}</caption>
      <tbody>
        @for (d of data(); track d.label) {
          <tr><th scope="row">{{ d.label }}</th><td>{{ d.created }}</td><td>{{ d.resolved }}</td></tr>
        }
      </tbody>
    </table>
  `,
})
export class TrendChart {
  readonly data = input.required<TrendDatum[]>();
  readonly title = input.required<string>();
  readonly createdLabel = input.required<string>();
  readonly resolvedLabel = input.required<string>();

  private readonly max = computed(() => Math.max(1, ...this.data().flatMap((d) => [d.created, d.resolved])));
  protected readonly created = computed(() => this.points((d) => d.created));
  protected readonly resolved = computed(() => this.points((d) => d.resolved));

  private points(pick: (d: TrendDatum) => number): string {
    const d = this.data();
    const step = d.length > 1 ? 300 / (d.length - 1) : 0;
    return d.map((x, i) => `${(i * step).toFixed(1)},${(95 - (pick(x) / this.max()) * 90).toFixed(1)}`).join(' ');
  }
}
