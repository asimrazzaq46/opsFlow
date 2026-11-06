import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { filter } from 'rxjs';
import { AuthService } from '../core/auth/auth.service';
import { ChatUnreadService, compactCount, unreadKey } from '../core/chat/chat-unread.service';
import { RouteAccess, canAccess, routeAccess } from '../core/auth/route-access';
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

  // Role-aware navigation shapes the UX only; the backend authorizes every request. Same rule as the route guard (`canAccess`).
  protected readonly navItems = computed(() => {
    const role = this.tenant.role();
    const items: { link: string; label: string; icon: string; access: RouteAccess }[] = [
      { link: '/dashboard', label: 'nav.dashboard', icon: 'dashboard', access: 'member' },
      { link: '/tickets', label: 'nav.tickets', icon: 'confirmation_number', access: 'member' },
      { link: '/kanban', label: 'nav.kanban', icon: 'view_kanban', access: 'staff' },
      { link: '/customers', label: 'nav.customers', icon: 'apartment', access: 'manager' },
      { link: '/team', label: 'nav.team', icon: 'group', access: 'staff' },
      { link: '/reports', label: 'nav.reports', icon: 'query_stats', access: 'manager' },
    ];
    return items.filter((i) => canAccess(i.access, role));
  });

  /** Routes flagged `publicLayout` (one-time e-mail links) never use the signed-in shell, even with a session open. */
  protected readonly publicLayout = signal(false);

  constructor() {
    const router = inject(Router);
    // Guards run only on navigation, but the active workspace, the role or the membership itself can change while a page stays open
    // (switcher, membership.changed, reconnect). Re-check the open page whenever that state settles and again whenever a navigation
    // settles, because a change that lands during a navigation may postdate that navigation's guard run. Nothing happens while the
    // memberships are loading, while a navigation is under way, or for pages outside a workspace; both targets are always allowed
    // (`/dashboard` for every member, `/workspaces` without access data), so this cannot loop.
    const revalidate = () => {
      if (!this.auth.isAuthenticated() || this.tenant.loading() || router.getCurrentNavigation()) return;
      const access = routeAccess(router.routerState.snapshot.root);
      if (!access) return;
      if (!this.tenant.tenantId()) void router.navigateByUrl('/workspaces');
      else if (!canAccess(access, this.tenant.role())) void router.navigateByUrl('/dashboard');
    };
    effect(() => {
      this.tenant.tenantId();
      this.tenant.role();
      this.tenant.loading();
      this.auth.isAuthenticated();
      untracked(revalidate);
    });
    router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd || e instanceof NavigationCancel || e instanceof NavigationError),
        takeUntilDestroyed(),
      )
      .subscribe(() => setTimeout(revalidate)); // after the router has finished the navigation (getCurrentNavigation() is null)
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
