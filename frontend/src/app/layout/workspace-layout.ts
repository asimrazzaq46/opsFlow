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
import { UnsavedChanges } from '../core/auth/unsaved-changes';
import { RealtimeService } from '../core/realtime/realtime.service';
import { ThemeService } from '../core/services/theme.service';
import { initials } from '../features/workspaces/workspace-api';
import { Language, LanguageService, SUPPORTED_LANGUAGES } from '../i18n/language.service';
import { ConfirmDialog } from '../shared/components/confirm-dialog/confirm-dialog';
import { HealthStatus } from '../shared/components/health-status';
import { NotificationCenter } from './notification-center';

@Component({
  selector: 'app-workspace-layout',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    NgTemplateOutlet,
    TranslatePipe,
    HealthStatus,
    NotificationCenter,
    ConfirmDialog,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-layout.html',
  styleUrl: './workspace-layout.css',
  host: { '(document:keydown.escape)': 'menuOpen() && !hasOpenDialog() && closeMenu()' },
})
export class WorkspaceLayout {
  protected readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  protected readonly unsaved = inject(UnsavedChanges);
  /** Warning text follows the form that is actually dirty; organization settings keep their own wording. */
  protected readonly switchMessageKey = computed(() =>
    !this.unsaved.dirty()
      ? 'tenant.switchMessage'
      : this.unsaved.kind() === 'deadlines'
        ? 'settings.sla.unsavedMessage'
        : 'tenant.switchUnsavedMessage',
  );
  protected readonly signOutMessageKey = computed(() =>
    !this.unsaved.dirty()
      ? 'auth.signOutMessage'
      : this.unsaved.kind() === 'deadlines'
        ? 'settings.sla.unsavedMessage'
        : 'auth.signOutUnsavedMessage',
  );
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
  private readonly tenantSelect = viewChild<ElementRef<HTMLSelectElement>>('tenantSelect');
  private readonly switchDialog = viewChild.required<ConfirmDialog>('switchDialog');
  private readonly logoutDialog = viewChild.required<ConfirmDialog>('logoutDialog');
  /** Workspace awaiting confirmation; null when no switch dialog is open. */
  protected readonly switchTarget = signal<{ id: string; name: string } | null>(null);
  private loggingOut = false;

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
      if (!this.auth.isAuthenticated() || this.tenant.loading() || router.getCurrentNavigation())
        return;
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
        filter(
          (e) =>
            e instanceof NavigationEnd ||
            e instanceof NavigationCancel ||
            e instanceof NavigationError,
        ),
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

  /** Escape belongs to an open confirmation dialog; it must not also close the sidebar and steal focus. */
  protected hasOpenDialog(): boolean {
    return !!document.querySelector('dialog[open]');
  }

  protected openMenu(): void {
    this.menuOpen.set(true);
    setTimeout(() => this.closeButton()?.nativeElement.focus());
  }

  protected closeMenu(restoreFocus = true): void {
    this.menuOpen.set(false);
    if (restoreFocus) this.menuButton()?.nativeElement.focus();
  }

  /** Only a different workspace asks for confirmation; the dropdown keeps showing the active one until the user confirms. */
  protected selectTenant(event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    const target = this.tenant.organizations().find((o) => o.id === id);
    if (!target || id === this.tenant.tenantId() || this.switchTarget()) {
      this.restoreTenantSelect();
      return;
    }
    this.switchTarget.set(target);
    this.switchDialog().open();
  }

  protected confirmSwitch(): void {
    const target = this.switchTarget();
    if (!target) return;
    this.switchTarget.set(null); // a second click on Confirm finds nothing to do
    this.tenant.select(target.id);
    this.switchDialog().close();
  }

  /** Runs on every close (Cancel, Escape, after Confirm): the select must mirror the workspace that is actually active. */
  protected switchClosed(): void {
    this.switchTarget.set(null);
    this.restoreTenantSelect();
  }

  protected askLogout(): void {
    this.logoutDialog().open();
  }

  protected confirmLogout(): void {
    if (this.loggingOut) return;
    this.loggingOut = true;
    this.logoutDialog().close();
    this.auth.logout();
  }

  private restoreTenantSelect(): void {
    const select = this.tenantSelect()?.nativeElement;
    if (select) select.value = this.tenant.tenantId() ?? '';
  }

  protected setLanguage(lang: Language): void {
    this.language.set(lang);
  }
}
