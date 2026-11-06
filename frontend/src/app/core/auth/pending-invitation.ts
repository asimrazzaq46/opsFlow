import { Injectable } from '@angular/core';
import { safeReturnUrl } from './return-url';

export const INVITATION_PATH = '/invitations/accept';

/** An invitation held in the browser only this long even if the invitation itself is valid for longer. */
const MAX_HOLD_MS = 24 * 60 * 60 * 1000;
const STORAGE_KEY = 'opsflow.invitation.pending';

/** The token of an in-app invitation link (`/invitations/accept?token=…`), or null for any other target. */
export function invitationTokenFrom(url: string | null | undefined): string | null {
  const safe = safeReturnUrl(url);
  if (!safe?.startsWith(`${INVITATION_PATH}?`)) return null;
  const token = new URLSearchParams(safe.slice(INVITATION_PATH.length + 1)).get('token');
  return token && token.length <= 200 ? token : null;
}

export function invitationUrl(token: string): string {
  return `${INVITATION_PATH}?token=${encodeURIComponent(token)}`;
}

/**
 * Remembers an invitation the visitor has opened until they have registered, confirmed their email (possibly in another tab) and signed
 * in. It is origin storage so the confirmation tab and the original tab agree; it expires, holds only the one-time token that the
 * visitor already has in the email, and is cleared once the invitation is accepted or found unusable. It grants nothing by itself:
 * acceptance still needs a signed-in click and the server checks the invited email.
 */
@Injectable({ providedIn: 'root' })
export class PendingInvitation {
  token(): string | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const value = JSON.parse(raw) as { token?: unknown; expiresAt?: unknown };
      if (typeof value.token === 'string' && typeof value.expiresAt === 'number' && value.expiresAt > Date.now()) return value.token;
      localStorage.removeItem(STORAGE_KEY);
    } catch {
      /* unreadable or unavailable storage: no pending invitation */
    }
    return null;
  }

  url(): string | null {
    const token = this.token();
    return token ? invitationUrl(token) : null;
  }

  /** `expiresAtUtc` is the invitation's own expiry when known; the hold time never exceeds it. */
  set(token: string, expiresAtUtc?: string | null): void {
    const invitationEnd = expiresAtUtc ? Date.parse(expiresAtUtc) : Number.NaN;
    const expiresAt = Math.min(Date.now() + MAX_HOLD_MS, Number.isNaN(invitationEnd) ? Infinity : invitationEnd);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ token, expiresAt }));
    } catch {
      /* storage unavailable: the invitation link in the URL still carries the journey */
    }
  }

  /** Clears only the given invitation (or whatever is pending when no token is given). */
  clear(token?: string): void {
    try {
      if (token === undefined || this.token() === token) localStorage.removeItem(STORAGE_KEY);
    } catch {
      /* nothing to clear */
    }
  }
}
