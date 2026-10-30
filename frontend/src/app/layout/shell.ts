import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../core/auth/auth.service';
import { TenantService } from '../core/auth/tenant.service';
import { RealtimeService } from '../core/realtime/realtime.service';
import { NotificationCenter } from './notification-center';
import { ThemeService } from '../core/services/theme.service';
import { LanguageService, SUPPORTED_LANGUAGES, Language } from '../i18n/language.service';
import { HealthStatus } from '../shared/components/health-status';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe, HealthStatus, NotificationCenter],
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

            <!-- UPDATED: login ke baad workspace picker, notification center aur sign-out -->
            @if (auth.isAuthenticated()) {
              @if (tenant.organizations().length) {
                <label class="sr-only" for="tenant-select">{{ 'tenant.label' | translate }}</label>
                <select id="tenant-select" class="max-w-32 rounded-lg border border-border bg-card px-2 py-1 text-sm focus-visible:outline-2 focus-visible:outline-brand sm:max-w-48"
                  (change)="selectTenant($event)">
                  @for (o of tenant.organizations(); track o.id) {
                    <option [value]="o.id" [selected]="o.id === tenant.tenantId()">{{ o.name }}</option>
                  }
                </select>
              }
              <app-notification-center />
              <button type="button" class="rounded-lg border border-border px-2.5 py-1 text-xs font-semibold focus-visible:outline-2 focus-visible:outline-brand" (click)="auth.logout()">
                {{ 'auth.signOut' | translate }}
              </button>
            }

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
        <!-- UPDATED: Milestone 5 - role-aware navigation (UX only; the backend enforces access) -->
        @if (auth.isAuthenticated()) {
          <nav class="mx-auto max-w-6xl overflow-x-auto px-4 sm:px-6" [attr.aria-label]="'nav.menu' | translate">
            <ul class="flex gap-1 pb-2 text-sm font-medium">
              @for (item of navItems(); track item.link) {
                <li><a [routerLink]="item.link" routerLinkActive="bg-brand text-brand-fg" class="block whitespace-nowrap rounded-lg px-3 py-1.5 text-muted hover:text-fg focus-visible:outline-2 focus-visible:outline-brand">{{ item.label | translate }}</a></li>
              }
            </ul>
          </nav>
        }
      </header>

      @if (auth.isAuthenticated() && realtime.state() !== 'connected') {
        <div role="status" class="bg-warning/20 px-4 py-1 text-center text-sm">
          {{ (realtime.state() === 'disconnected' ? 'realtime.disconnected' : 'realtime.reconnecting') | translate }}
        </div>
      }

      <main class="mx-auto w-full max-w-6xl flex-1 px-4 py-10 sm:px-6 sm:py-16">
        <router-outlet />
      </main>
    </div>
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  protected readonly realtime = inject(RealtimeService);
  protected readonly theme = inject(ThemeService);
  protected readonly language = inject(LanguageService);
  protected readonly languages = SUPPORTED_LANGUAGES;

  protected readonly navItems = computed(() => {
    const role = this.tenant.role();
    const staff = role !== null && role !== 'Customer';
    const manager = role === 'TenantOwner' || role === 'Manager';
    return [
      { link: '/dashboard', label: 'nav.dashboard' },
      { link: '/tickets', label: 'nav.tickets' },
      ...(staff ? [{ link: '/kanban', label: 'nav.kanban' }] : []),
      ...(manager ? [{ link: '/customers', label: 'nav.customers' }, { link: '/reports', label: 'nav.reports' }] : []),
      ...(staff ? [{ link: '/team', label: 'nav.team' }] : []),
    ];
  });

  protected selectTenant(event: Event): void {
    this.tenant.select((event.target as HTMLSelectElement).value);
  }

  protected setLanguage(lang: Language): void {
    this.language.set(lang);
  }
}
