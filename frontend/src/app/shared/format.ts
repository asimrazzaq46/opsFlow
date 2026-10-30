import { TicketStatus } from '../core/api/generated/models';

/** GraphQL enums arrive as UPPER_SNAKE; REST and the translation keys use PascalCase. */
export const pascal = (value: string): string =>
  value.toLowerCase().split('_').map((p) => p.charAt(0).toUpperCase() + p.slice(1)).join('');

export const toRestStatus = (value: string): TicketStatus => pascal(value) as TicketStatus;

export const isoDay = (d: Date): string => d.toISOString().slice(0, 10);

export function defaultRange(days = 30): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to.getTime() - (days - 1) * 86_400_000);
  return { from: isoDay(from), to: isoDay(to) };
}

export const minutesLabel = (m: number | null | undefined): string | null => {
  if (m === null || m === undefined) return null;
  return m < 90 ? `${Math.round(m)} min` : m < 2880 ? `${(m / 60).toFixed(1)} h` : `${(m / 1440).toFixed(1)} d`;
};
