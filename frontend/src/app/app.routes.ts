import { Routes } from '@angular/router';
import { authGuard, emailLinkGuard, guestGuard, workspaceGuard } from './core/auth/auth.guard';

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
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/tickets/ticket-list').then((m) => m.TicketList),
  },
  {
    path: 'tickets/new',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/tickets/ticket-create').then((m) => m.TicketCreate),
  },
  {
    path: 'tickets/:id',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/tickets/ticket-detail').then((m) => m.TicketDetail),
  },
  {
    path: 'dashboard',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'kanban',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/kanban/kanban').then((m) => m.Kanban),
  },
  {
    path: 'reports',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/reports/reports').then((m) => m.Reports),
  },
  {
    path: 'customers',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/customers/customers').then((m) => m.Customers),
  },
  {
    path: 'account',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/account/account-security').then((m) => m.AccountSecurity),
  },
  {
    path: 'team',
    canActivate: [authGuard, workspaceGuard],
    loadComponent: () => import('./features/team/team').then((m) => m.Team),
  },
  { path: '**', redirectTo: '' },
];
