import { mergeBySequence } from './merge';
import { HttpErrorResponse } from '@angular/common/http';
import { attachmentErrorKey, attachmentProblem } from './ticket-attachments';

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

describe('attachmentErrorKey', () => {
  const err = (status: number, code?: string) => new HttpErrorResponse({ status, error: code ? { code } : null });
  it('prefers the server file codes, then the HTTP status', () => {
    expect(attachmentErrorKey(err(400, 'ATTACHMENT_TOO_LARGE'), 'uploadError')).toBe('tooLarge');
    expect(attachmentErrorKey(err(400, 'ATTACHMENT_TYPE_NOT_ALLOWED'), 'uploadError')).toBe('badType');
    expect(attachmentErrorKey(err(400), 'uploadError')).toBe('invalid');
    expect(attachmentErrorKey(err(403), 'uploadError')).toBe('forbidden');
    expect(attachmentErrorKey(err(409), 'uploadError')).toBe('conflict');
    expect(attachmentErrorKey(err(0), 'uploadError')).toBe('network');
    expect(attachmentErrorKey(err(500), 'downloadError')).toBe('downloadError');
  });
});
