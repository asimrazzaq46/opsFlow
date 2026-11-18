/** Server limits: whole calendar minutes, 1..one year. */
export const SLA_MIN_MINUTES = 1;
export const SLA_MAX_MINUTES = 525_600;

export type SlaUnit = 'minutes' | 'hours' | 'days';
export const SLA_UNITS: readonly SlaUnit[] = ['minutes', 'hours', 'days'];

const UNIT_MINUTES: Record<SlaUnit, number> = { minutes: 1, hours: 60, days: 1440 };

/** Largest unit that represents the value exactly, so editing never rounds a stored target (90 min stays 90 minutes). */
export function splitMinutes(minutes: number): { amount: number; unit: SlaUnit } {
  const unit: SlaUnit = minutes % 1440 === 0 ? 'days' : minutes % 60 === 0 ? 'hours' : 'minutes';
  return { amount: minutes / UNIT_MINUTES[unit], unit };
}

/**
 * Exact conversion to integer minutes; null when the amount is empty, not finite or does not map to a whole number of minutes
 * (e.g. 0.0001 hours). The tolerance only absorbs binary floating point noise such as 0.1 day = 144.00000000000003.
 */
export function toMinutes(amount: number | null, unit: SlaUnit): number | null {
  if (amount === null || !Number.isFinite(amount)) return null;
  const minutes = amount * UNIT_MINUTES[unit];
  const rounded = Math.round(minutes);
  return Math.abs(minutes - rounded) < 1e-6 ? rounded : null;
}

export type SlaTargetError = 'invalid' | 'range';

/** Per-target check shared by the form hints; `null` means the minutes are acceptable. */
export function targetError(minutes: number | null): SlaTargetError | null {
  if (minutes === null) return 'invalid';
  return minutes < SLA_MIN_MINUTES || minutes > SLA_MAX_MINUTES ? 'range' : null;
}
