import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import { getCurrentUser, linkGoogleAccount, listSessions, logoutAll, revokeSession, updateCurrentUser } from '../../core/api/generated/functions';
import { SessionResponse, UserProfileDto } from '../../core/api/generated/models';
import { GoogleCredential } from '../../core/auth/google-identity.service';

/** The signed-in user's own account; the user is always taken from the access token by the API. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly api = inject(Api);

  profile(): Observable<UserProfileDto> {
    return this.api.invoke(getCurrentUser, {});
  }

  updateDisplayName(displayName: string): Observable<UserProfileDto> {
    return this.api.invoke(updateCurrentUser, { body: { displayName } });
  }

  linkGoogle(credential: GoogleCredential): Observable<void> {
    return this.api.invoke(linkGoogleAccount, { body: credential });
  }

  sessions(): Observable<SessionResponse[]> {
    return this.api.invoke(listSessions, {});
  }

  revokeSession(sessionId: string): Observable<void> {
    return this.api.invoke(revokeSession, { sessionId });
  }

  logoutAll(): Observable<void> {
    return this.api.invoke(logoutAll, {});
  }
}
