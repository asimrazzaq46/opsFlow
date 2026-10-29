import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ThemeService } from '../core/services/theme.service';
import { LanguageService, SUPPORTED_LANGUAGES, Language } from '../i18n/language.service';
import { HealthStatus } from '../shared/components/health-status';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, TranslatePipe, HealthStatus],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex min-h-screen flex-col">
      <header class="sticky top-0 z-10 border-b border-border bg-card/80 backdrop-blur">
        <div class="mx-auto flex h-16 max-w-6xl items-center justify-between gap-3 px-4 sm:px-6">
          <a href="/" class="flex items-center gap-2 text-lg font-semibold tracking-tight">
            <span
              class="grid size-8 place-items-center rounded-lg bg-brand text-sm font-bold text-brand-fg"
              aria-hidden="true"
              >O</span
            >
            OpsFlow
          </a>

          <div class="flex items-center gap-2 sm:gap-3">
            <app-health-status class="hidden sm:inline-flex" />

            <div role="group" [attr.aria-label]="'shell.language' | translate" class="flex rounded-lg border border-border p-0.5">
              @for (lang of languages; track lang) {
                <button
                  type="button"
                  class="rounded-md px-2.5 py-1 text-xs font-semibold uppercase focus-visible:outline-2 focus-visible:outline-brand"
                  [class]="language.current() === lang ? 'bg-brand text-brand-fg' : 'text-muted hover:text-fg'"
                  [attr.aria-pressed]="language.current() === lang"
                  (click)="setLanguage(lang)"
                >
                  {{ lang }}
                </button>
              }
            </div>

            <button
              type="button"
              class="grid size-9 place-items-center rounded-lg border border-border text-muted hover:text-fg focus-visible:outline-2 focus-visible:outline-brand"
              [attr.aria-label]="'shell.toggleTheme' | translate"
              (click)="theme.toggle()"
            >
              <span aria-hidden="true">{{ theme.theme() === 'dark' ? '☀' : '☾' }}</span>
            </button>
          </div>
        </div>
      </header>

      <main class="mx-auto w-full max-w-6xl flex-1 px-4 py-10 sm:px-6 sm:py-16">
        <router-outlet />
      </main>
    </div>
  `,
})
export class Shell {
  protected readonly theme = inject(ThemeService);
  protected readonly language = inject(LanguageService);
  protected readonly languages = SUPPORTED_LANGUAGES;

  protected setLanguage(lang: Language): void {
    this.language.set(lang);
  }
}
