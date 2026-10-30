import { mergeBySequence } from './merge';
import { attachmentProblem } from './ticket-attachments';

describe('mergeBySequence', () => {
  it('ignores ids that are already present and keeps sequence order', () => {
    const base = [
      { id: 'a', sequence: 1 },
      { id: 'c', sequence: '3' },
    ];
    const merged = mergeBySequence(base, [
      { id: 'c', sequence: 3 },
      { id: 'b', sequence: 2 },
    ]);
    expect(merged.map((m) => m.id)).toEqual(['a', 'b', 'c']);
    expect(mergeBySequence(merged, [{ id: 'b', sequence: 2 }])).toHaveLength(3);
  });
});

describe('attachmentProblem', () => {
  it('accepts allowed types and rejects others and oversized files', () => {
    expect(attachmentProblem({ name: 'a.PNG', size: 10 })).toBeNull();
    expect(attachmentProblem({ name: 'a.exe', size: 10 })).toBe('badType');
    expect(attachmentProblem({ name: 'noext', size: 10 })).toBe('badType');
    expect(attachmentProblem({ name: 'a.pdf', size: 10 * 1024 * 1024 + 1 })).toBe('tooLarge');
  });
});
