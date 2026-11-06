import { PendingInvitation, invitationTokenFrom, invitationUrl } from './pending-invitation';

describe('invitation links', () => {
  it('reads the token only from an in-app invitation path', () => {
    expect(invitationTokenFrom(invitationUrl('a b/c'))).toBe('a b/c');
    expect(invitationTokenFrom('/tickets?token=x')).toBeNull();
    expect(invitationTokenFrom('//evil.test/invitations/accept?token=x')).toBeNull();
    expect(invitationTokenFrom('https://evil.test/invitations/accept?token=x')).toBeNull();
    expect(invitationTokenFrom(null)).toBeNull();
  });
});

describe('PendingInvitation', () => {
  let pending: PendingInvitation;

  beforeEach(() => {
    localStorage.clear();
    pending = new PendingInvitation();
  });

  it('keeps an invitation until it is cleared', () => {
    pending.set('tok');
    expect(pending.token()).toBe('tok');
    expect(pending.url()).toBe('/invitations/accept?token=tok');
    pending.clear('other');
    expect(pending.token()).toBe('tok'); // another invitation's outcome must not drop it
    pending.clear('tok');
    expect(pending.token()).toBeNull();
  });

  it('never outlives the invitation itself or the hold limit', () => {
    pending.set('tok', new Date(Date.now() - 1000).toISOString());
    expect(pending.token()).toBeNull();
    pending.set('tok', new Date(Date.now() + 365 * 24 * 3600 * 1000).toISOString());
    const stored = JSON.parse(localStorage.getItem('opsflow.invitation.pending')!) as { expiresAt: number };
    expect(stored.expiresAt).toBeLessThanOrEqual(Date.now() + 24 * 3600 * 1000);
  });

  it('ignores a corrupted record', () => {
    localStorage.setItem('opsflow.invitation.pending', '{nope');
    expect(pending.token()).toBeNull();
  });
});
