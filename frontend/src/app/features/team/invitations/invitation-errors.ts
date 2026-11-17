import { HttpErrorResponse } from '@angular/common/http';
import { TimeoutError } from 'rxjs';

export type InvitationOperation = 'create' | 'resend' | 'revoke';

export function problemCode(e: HttpErrorResponse): string | undefined {
  const code = (e.error as { code?: unknown } | null)?.code;
  return typeof code === 'string' ? code : undefined;
}

/** The server status and problem code decide the message; unknown failures get a generic one. */
export function invitationErrorKey(e: unknown, operation: InvitationOperation): string {
  if (e instanceof TimeoutError) return 'invitations.timeout';
  if (!(e instanceof HttpErrorResponse)) return 'invitations.error';
  const code = problemCode(e);
  switch (e.status) {
    case 400:
      return 'invitations.errors.invalid';
    case 403:
      return 'common.forbidden';
    case 404:
      return 'invitations.errors.notFound';
    case 409:
      if (code === 'INVITATION_ALREADY_MEMBER') return 'invitations.errors.alreadyMember';
      if (code === 'INVITATION_NOT_RESENDABLE') return 'invitations.errors.notResendable';
      // Duplicate-pending, accepted and concurrent changes carry no stable code on these operations.
      return `invitations.errors.${operation}Conflict`;
    case 429:
      return code === 'INVITATION_RESEND_COOLDOWN' ? 'invitations.errors.cooldown' : 'invitations.errors.rateLimited';
    default:
      return 'invitations.error';
  }
}

/** After a vanished invitation or a state conflict the list no longer matches the server and must be reloaded. */
export function invitationStateChanged(e: unknown): boolean {
  return (
    e instanceof HttpErrorResponse &&
    (e.status === 404 || (e.status === 409 && problemCode(e) !== 'INVITATION_ALREADY_MEMBER'))
  );
}
