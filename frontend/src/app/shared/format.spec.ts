import { describe, expect, it } from 'vitest';
import { defaultRange, minutesLabel, pascal, toRestStatus } from './format';

describe('format helpers', () => {
  it('maps GraphQL enum names to REST/translation names', () => {
    expect(pascal('IN_PROGRESS')).toBe('InProgress');
    expect(toRestStatus('ON_HOLD')).toBe('OnHold');
  });

  it('formats durations and keeps N/A (null) distinct from zero', () => {
    expect(minutesLabel(null)).toBeNull();
    expect(minutesLabel(0)).toBe('0 min');
    expect(minutesLabel(180)).toBe('3.0 h');
  });

  it('defaults to the last 30 calendar days including today', () => {
    const { from, to } = defaultRange(30);
    expect((new Date(to).getTime() - new Date(from).getTime()) / 86_400_000).toBe(29);
  });
});
