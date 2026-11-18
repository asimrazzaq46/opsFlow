import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { filter, map, startWith } from 'rxjs';
import { RouteAccess, canAccess } from '../../../core/auth/route-access';
import { TenantService } from '../../../core/auth/tenant.service';

type TabId = 'categories' | 'deadlines';

interface Tab {
  id: TabId;
  icon: string;
  label: string;
  access: RouteAccess;
}

const TABS: readonly Tab[] = [
  { id: 'categories', icon: 'category', label: 'settings.tickets.tabs.categories', access: 'manager' },
  { id: 'deadlines', icon: 'timer', label: 'settings.tickets.tabs.deadlines', access: 'staff' },
];

/**
 * Shell of the ticket settings: one header and a tab bar over the two feature pages, which stay separate routed components. The tab
 * is the URL, so Back/Forward, bookmarks and the route guards (including the unsaved-changes prompt of the deadlines page) apply
 * to tab switches exactly as to any other navigation. The tabs only mirror the route access; the server authorizes every request.
 */
@Component({
  selector: 'app-ticket-settings',
  imports: [RouterOutlet, TranslatePipe],
  templateUrl: './ticket-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketSettings {
  private readonly router = inject(Router);
  private readonly tenant = inject(TenantService);
  private readonly tablist = viewChild.required<ElementRef<HTMLElement>>('tablist');

  protected readonly tabs = computed(() => TABS.filter((t) => canAccess(t.access, this.tenant.role())));
  private readonly path = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url),
    ),
    { requireSync: true },
  );
  protected readonly active = computed<TabId>(() =>
    this.path().split(/[?#]/)[0].endsWith('/deadlines') ? 'deadlines' : 'categories',
  );

  protected select(id: TabId): void {
    void this.router.navigate(['/settings/tickets', id]);
  }

  /** Arrow keys only move focus: activating a tab can open the unsaved-changes prompt, so Enter/Space (or a click) decides. */
  protected onKeydown(event: KeyboardEvent): void {
    const tabs = [...this.tablist().nativeElement.querySelectorAll<HTMLElement>('[role="tab"]')];
    const current = tabs.indexOf(document.activeElement as HTMLElement);
    if (current < 0) return;
    const next =
      event.key === 'ArrowRight'
        ? (current + 1) % tabs.length
        : event.key === 'ArrowLeft'
          ? (current - 1 + tabs.length) % tabs.length
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? tabs.length - 1
              : -1;
    if (next < 0) return;
    tabs[next].focus();
    event.preventDefault();
  }
}
