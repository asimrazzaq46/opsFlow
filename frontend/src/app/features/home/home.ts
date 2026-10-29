import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { HealthStatus } from '../../shared/components/health-status';

@Component({
  selector: 'app-home',
  imports: [TranslatePipe, HealthStatus],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="relative overflow-hidden rounded-3xl border border-border bg-card px-6 py-16 text-center shadow-sm sm:px-12 sm:py-24">
      <div
        class="pointer-events-none absolute inset-x-0 -top-24 mx-auto h-64 max-w-xl rounded-full bg-brand/20 blur-3xl"
        aria-hidden="true"
      ></div>
      <div class="relative">
        <p class="text-sm font-semibold uppercase tracking-widest text-brand">{{ 'home.eyebrow' | translate }}</p>
        <h1 class="mx-auto mt-4 max-w-2xl text-4xl font-bold tracking-tight sm:text-5xl">{{ 'home.title' | translate }}</h1>
        <p class="mx-auto mt-5 max-w-xl text-base text-muted sm:text-lg">{{ 'home.subtitle' | translate }}</p>
        <div class="mt-8 flex justify-center sm:hidden"><app-health-status /></div>
      </div>
    </section>
  `,
})
export class Home {}
