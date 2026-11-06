import { HttpErrorResponse } from '@angular/common/http';
import { TicketPriority, TicketStatus } from '../../core/api/generated/models';

export const TICKET_STATUSES: TicketStatus[] = [
  'Open',
  'Assigned',
  'InProgress',
  'OnHold',
  'Resolved',
  'Closed',
];
export const TICKET_PRIORITIES: TicketPriority[] = ['Low', 'Medium', 'High', 'Urgent'];

// Badges always carry their text label; color only reinforces it.
export const STATUS_BADGE: Record<TicketStatus, string> = {
  Open: 'bg-primary-fixed text-on-primary-fixed',
  Assigned: 'bg-surface-container-high text-on-surface',
  InProgress: 'bg-primary-container text-on-primary',
  OnHold: 'bg-tertiary/15 text-tertiary',
  Resolved: 'bg-secondary-container text-on-secondary-container',
  Closed: 'bg-surface-container text-on-surface-variant',
};

export const PRIORITY_BADGE: Record<TicketPriority, { badge: string; dot: string }> = {
  Urgent: { badge: 'bg-error-container text-on-error-container', dot: 'bg-error' },
  High: { badge: 'bg-tertiary/15 text-tertiary', dot: 'bg-tertiary' },
  Medium: { badge: 'bg-primary-fixed text-on-primary-fixed', dot: 'bg-primary' },
  Low: { badge: 'bg-surface-container text-on-surface-variant', dot: 'bg-outline' },
};

export const httpStatus = (e: unknown): number => (e instanceof HttpErrorResponse ? e.status : 0);

/** Stable `code` of an RFC 9457 problem body, when the server sent one. */
export const problemCode = (e: unknown): string | undefined =>
  e instanceof HttpErrorResponse
    ? ((e.error as { code?: string } | null)?.code ?? undefined)
    : undefined;

/** First message per field of a 400 validation problem, keyed by the server's (PascalCase) field name. */
export function fieldErrors(e: unknown): Record<string, string> {
  if (!(e instanceof HttpErrorResponse) || e.status !== 400) return {};
  const errors = (e.error as { errors?: Record<string, string[]> } | null)?.errors;
  return errors ? Object.fromEntries(Object.entries(errors).map(([k, m]) => [k, m[0] ?? ''])) : {};
}
