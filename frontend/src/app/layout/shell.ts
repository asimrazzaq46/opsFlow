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
  templateUrl: './shell.html',
  styleUrl: './shell.css',
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
