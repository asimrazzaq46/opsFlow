import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/home/home').then((m) => m.Home),
  },
  // UPDATED: Milestone 3 - login, ticket list/detail (chat, notes, attachments)
  { path: 'login', loadComponent: () => import('./features/auth/login').then((m) => m.Login) },
  { path: 'tickets', canActivate: [authGuard], loadComponent: () => import('./features/tickets/ticket-list').then((m) => m.TicketList) },
  { path: 'tickets/new', canActivate: [authGuard], loadComponent: () => import('./features/tickets/ticket-create').then((m) => m.TicketCreate) },
  { path: 'tickets/:id', canActivate: [authGuard], loadComponent: () => import('./features/tickets/ticket-detail').then((m) => m.TicketDetail) },
  // UPDATED: Milestone 5 - dashboard, board, reports, customers, team, new ticket
  { path: 'dashboard', canActivate: [authGuard], loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard) },
  { path: 'kanban', canActivate: [authGuard], loadComponent: () => import('./features/kanban/kanban').then((m) => m.Kanban) },
  { path: 'reports', canActivate: [authGuard], loadComponent: () => import('./features/reports/reports').then((m) => m.Reports) },
  { path: 'customers', canActivate: [authGuard], loadComponent: () => import('./features/customers/customers').then((m) => m.Customers) },
  { path: 'team', canActivate: [authGuard], loadComponent: () => import('./features/team/team').then((m) => m.Team) },
  { path: '**', redirectTo: '' },
];
