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
  { path: 'tickets/:id', canActivate: [authGuard], loadComponent: () => import('./features/tickets/ticket-detail').then((m) => m.TicketDetail) },
  { path: '**', redirectTo: '' },
];
