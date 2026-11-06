import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { filter } from 'rxjs';
import { AuthService } from '../core/auth/auth.service';
import { ChatUnreadService, compactCount, unreadKey } from '../core/chat/chat-unread.service';
import { TenantService } from '../core/auth/tenant.service';
import { RealtimeService } from '../core/realtime/realtime.service';
import { ThemeService } from '../core/services/theme.service';
import { initials } from '../features/workspaces/workspace-api';
import { Language, LanguageService, SUPPORTED_LANGUAGES } from '../i18n/language.service';
import { HealthStatus } from '../shared/components/health-status';
import { NotificationCenter } from './notification-center';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    NgTemplateOutlet,
    TranslatePipe,
    HealthStatus,
    NotificationCenter,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.css',
  host: { '(document:keydown.escape)': 'menuOpen() && closeMenu()' },
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  protected readonly realtime = inject(RealtimeService);
  protected readonly theme = inject(ThemeService);
  protected readonly language = inject(LanguageService);
  protected readonly chatUnread = inject(ChatUnreadService);
  protected readonly unreadKey = unreadKey;
  protected readonly compactCount = compactCount;
  protected readonly languages = SUPPORTED_LANGUAGES;
  protected readonly initials = initials;

  /** Off-canvas sidebar below the `lg` breakpoint; on larger screens the sidebar is always visible. */
  protected readonly menuOpen = signal(false);
  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');

  // Role-aware navigation shapes the UX only; the backend authorizes every request.
  protected readonly navItems = computed(() => {
    const role = this.tenant.role();
    const staff = role !== null && role !== 'Customer';
    const manager = role === 'TenantOwner' || role === 'Manager';
    return [
      { link: '/dashboard', label: 'nav.dashboard', icon: 'dashboard' },
      { link: '/tickets', label: 'nav.tickets', icon: 'confirmation_number' },
      ...(staff ? [{ link: '/kanban', label: 'nav.kanban', icon: 'view_kanban' }] : []),
      ...(manager ? [{ link: '/customers', label: 'nav.customers', icon: 'apartment' }] : []),
      ...(staff ? [{ link: '/team', label: 'nav.team', icon: 'group' }] : []),
      ...(manager ? [{ link: '/reports', label: 'nav.reports', icon: 'query_stats' }] : []),
    ];
  });

  /** Routes flagged `publicLayout` (one-time e-mail links) never use the signed-in shell, even with a session open. */
  protected readonly publicLayout = signal(false);

  constructor() {
    const router = inject(Router);
    router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.menuOpen.set(false);
        let route = router.routerState.snapshot.root;
        while (route.firstChild) route = route.firstChild;
        this.publicLayout.set(route.data['publicLayout'] === true);
      });
  }

  protected openMenu(): void {
    this.menuOpen.set(true);
    setTimeout(() => this.closeButton()?.nativeElement.focus());
  }

  protected closeMenu(restoreFocus = true): void {
    this.menuOpen.set(false);
    if (restoreFocus) this.menuButton()?.nativeElement.focus();
  }

  protected selectTenant(event: Event): void {
    this.tenant.select((event.target as HTMLSelectElement).value);
  }

  protected setLanguage(lang: Language): void {
    this.language.set(lang);
  }
}
