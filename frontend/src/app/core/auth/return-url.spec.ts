import { safeReturnUrl } from './return-url';

describe('safeReturnUrl', () => {
  it('keeps in-app paths including query strings', () => {
    expect(safeReturnUrl('/invitations/accept?token=abc')).toBe('/invitations/accept?token=abc');
    expect(safeReturnUrl('/tickets/42')).toBe('/tickets/42');
  });

  it('rejects anything that could leave the app', () => {
    for (const bad of [null, '', 'https://evil.test', '//evil.test', '/\\evil.test', 'javascript:alert(1)', 'tickets', '/a\\b', '/a\nb']) {
      expect(safeReturnUrl(bad)).toBeNull();
    }
  });
});
