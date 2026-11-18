import { inject } from '@angular/core';
import { Router, Routes } from '@angular/router';
import { accessGuard, authGuard, emailLinkGuard, guestGuard, notificationWorkspaceGuard } from './core/auth/auth.guard';
import { RouteAccess } from './core/auth/route-access';
import { TenantService } from './core/auth/tenant.service';
import type { OrganizationSettings } from './features/settings/organization-settings';
import type { SlaPolicies } from './features/settings/sla-policies/sla-policies';

/** `data.access` of a workspace page; the guard, the sidebar and the shell's live revalidation all read it through `canAccess`. */
const access = (level: RouteAccess) => ({ access: level });

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/home/home').then((m) => m.Home),
  },
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/login').then((m) => m.Login),
  },
  {
    path: 'register',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/register/register').then((m) => m.Register),
  },
  {
    path: 'forgot-password',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./features/auth/forgot-password/forgot-password').then((m) => m.ForgotPassword),
  },
  // Paths below are fixed by the links the API puts in its emails.
  {
    path: 'auth/confirm-email',
    canActivate: [emailLinkGuard],
    data: { publicLayout: true },
    loadComponent: () =>
      import('./features/auth/confirm-email/confirm-email').then((m) => m.ConfirmEmail),
  },
  {
    path: 'auth/reset-password',
    canActivate: [emailLinkGuard],
    data: { publicLayout: true },
    loadComponent: () =>
      import('./features/auth/reset-password/reset-password').then((m) => m.ResetPassword),
  },
  {
    path: 'invitations/accept',
    loadComponent: () =>
      import('./features/workspaces/accept-invitation/accept-invitation').then(
        (m) => m.AcceptInvitation,
      ),
  },
  {
    path: 'workspaces',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/workspaces/workspace-select/workspace-select').then(
        (m) => m.WorkspaceSelect,
      ),
  },
  {
    path: 'workspaces/new',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/workspaces/workspace-setup/workspace-setup').then((m) => m.WorkspaceSetup),
  },
  {
    path: 'tickets',
    canActivate: [authGuard, accessGuard],
    data: access('member'),
    loadComponent: () => import('./features/tickets/ticket-list').then((m) => m.TicketList),
  },
  {
    path: 'tickets/new',
    canActivate: [authGuard, accessGuard],
    data: access('member'),
    loadComponent: () => import('./features/tickets/ticket-create').then((m) => m.TicketCreate),
  },
  {
    path: 'tickets/:id',
    canActivate: [authGuard, notificationWorkspaceGuard], // includes the accessGuard check
    data: access('member'),
    loadComponent: () => import('./features/tickets/ticket-detail').then((m) => m.TicketDetail),
  },
  {
    path: 'dashboard',
    canActivate: [authGuard, accessGuard],
    data: access('member'),
    loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'notifications',
    canActivate: [authGuard, accessGuard],
    data: access('member'),
    loadComponent: () =>
      import('./features/notifications/notification-history').then((m) => m.NotificationHistory),
  },
  {
    path: 'kanban',
    canActivate: [authGuard, accessGuard],
    data: access('staff'),
    loadComponent: () => import('./features/kanban/kanban').then((m) => m.Kanban),
  },
  {
    path: 'reports',
    canActivate: [authGuard, accessGuard],
    data: access('manager'),
    loadComponent: () => import('./features/reports/reports').then((m) => m.Reports),
  },
  {
    path: 'customers',
    canActivate: [authGuard, accessGuard],
    data: access('manager'),
    loadComponent: () => import('./features/customers/customers').then((m) => m.Customers),
  },
  {
    path: 'customers/new',
    canActivate: [authGuard, accessGuard],
    data: access('manager'),
    loadComponent: () =>
      import('./features/customers/customer-form').then((m) => m.CustomerForm),
  },
  {
    path: 'customers/:id',
    canActivate: [authGuard, accessGuard],
    data: access('manager'),
    loadComponent: () =>
      import('./features/customers/customer-detail').then((m) => m.CustomerDetail),
  },
  {
    path: 'customers/:id/edit',
    canActivate: [authGuard, accessGuard],
    data: access('manager'),
    loadComponent: () =>
      import('./features/customers/customer-form').then((m) => m.CustomerForm),
  },
  {
    path: 'account',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/account/account-security').then((m) => m.AccountSecurity),
  },
  {
    path: 'team',
    canActivate: [authGuard, accessGuard],
    data: access('staff'),
    loadComponent: () => import('./features/team/team').then((m) => m.Team),
  },
  {
    path: 'team/:membershipId',
    canActivate: [authGuard, accessGuard],
    data: access('staff'),
    loadComponent: () =>
      import('./features/team/member-detail/member-detail').then((m) => m.MemberDetail),
  },
  {
    path: 'settings',
    canActivate: [authGuard, accessGuard],
    canDeactivate: [(page: OrganizationSettings) => page.confirmLeave()],
    data: access('staff'),
    loadComponent: () =>
      import('./features/settings/organization-settings').then((m) => m.OrganizationSettings),
  },
  // Bookmarks of the former separate pages keep working.
  { path: 'settings/categories', redirectTo: '/settings/tickets/categories' },
  { path: 'settings/sla-policies', redirectTo: '/settings/tickets/deadlines' },
  {
    path: 'settings/tickets',
    canActivate: [authGuard, accessGuard],
    data: access('staff'),
    loadComponent: () =>
      import('./features/settings/ticket-settings/ticket-settings').then((m) => m.TicketSettings),
    children: [
      {
        // Runs after the parent guards, so the role is known: Managers start on Categories, Agents on Deadlines.
        path: '',
        pathMatch: 'full',
        canActivate: [
          () =>
            inject(Router).createUrlTree([
              '/settings/tickets',
              inject(TenantService).isManager() ? 'categories' : 'deadlines',
            ]),
        ],
        children: [],
      },
      {
        path: 'categories',
        canActivate: [accessGuard],
        data: access('manager'),
        loadComponent: () =>
          import('./features/settings/categories/ticket-categories').then((m) => m.TicketCategories),
      },
      {
        path: 'deadlines',
        canDeactivate: [(page: SlaPolicies) => page.confirmLeave()],
        data: access('staff'),
        loadComponent: () =>
          import('./features/settings/sla-policies/sla-policies').then((m) => m.SlaPolicies),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
