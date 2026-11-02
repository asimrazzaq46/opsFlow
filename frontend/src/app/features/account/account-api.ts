import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import { getCurrentUser, linkGoogleAccount } from '../../core/api/generated/functions';
import { UserProfileDto } from '../../core/api/generated/models';
import { GoogleCredential } from '../../core/auth/google-identity.service';

/** The signed-in user's own account; the user is always taken from the access token by the API. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly api = inject(Api);

  profile(): Observable<UserProfileDto> {
    return this.api.invoke(getCurrentUser, {});
  }

  linkGoogle(credential: GoogleCredential): Observable<void> {
    return this.api.invoke(linkGoogleAccount, { body: credential });
  }
}
