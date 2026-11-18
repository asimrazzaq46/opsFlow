import { SLA_MAX_MINUTES, splitMinutes, targetError, toMinutes } from './sla-duration';

describe('sla-duration', () => {
  it('keeps stored targets exact when splitting into a unit', () => {
    expect(splitMinutes(4320)).toEqual({ amount: 3, unit: 'days' });
    expect(splitMinutes(240)).toEqual({ amount: 4, unit: 'hours' });
    expect(splitMinutes(90)).toEqual({ amount: 90, unit: 'minutes' });
  });

  it('converts to whole minutes and rejects fractional results', () => {
    expect(toMinutes(1.5, 'hours')).toBe(90);
    expect(toMinutes(0.1, 'days')).toBe(144);
    expect(toMinutes(0.0001, 'hours')).toBeNull();
    expect(toMinutes(null, 'minutes')).toBeNull();
    expect(toMinutes(Number.NaN, 'days')).toBeNull();
  });

  it('enforces the server range', () => {
    expect(targetError(0)).toBe('range');
    expect(targetError(SLA_MAX_MINUTES)).toBeNull();
    expect(targetError(SLA_MAX_MINUTES + 1)).toBe('range');
    expect(targetError(null)).toBe('invalid');
  });
});
