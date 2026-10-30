import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export interface ChartDatum {
  label: string;
  value: number;
}

/** Dependency-free horizontal bar chart; the visually hidden table is the screen-reader equivalent. */
@Component({
  selector: 'app-bar-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div role="img" [attr.aria-label]="title()" class="space-y-2">
      @for (d of data(); track d.label) {
        <div class="grid grid-cols-[minmax(5rem,9rem)_1fr_auto] items-center gap-2 text-sm">
          <span class="truncate text-muted" [title]="d.label">{{ d.label }}</span>
          <span class="h-3 overflow-hidden rounded-full bg-surface">
            <span class="block h-full rounded-full bg-brand" [style.width.%]="(d.value / max()) * 100"></span>
          </span>
          <span class="tabular-nums font-medium">{{ d.value }}</span>
        </div>
      }
    </div>
    <table class="sr-only">
      <caption>{{ title() }}</caption>
      <tbody>
        @for (d of data(); track d.label) {
          <tr><th scope="row">{{ d.label }}</th><td>{{ d.value }}</td></tr>
        }
      </tbody>
    </table>
  `,
})
export class BarChart {
  readonly data = input.required<ChartDatum[]>();
  readonly title = input.required<string>();
  protected readonly max = computed(() => Math.max(1, ...this.data().map((d) => d.value)));
}
