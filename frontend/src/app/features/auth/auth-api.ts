import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { AbstractControl, ValidationErrors } from '@angular/forms';
import { Observable, TimeoutError, timeout } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  confirmEmail,
  forgotPassword,
  register,
  resendConfirmation,
  resetPassword,
} from '../../core/api/generated/functions';

/** Password rules enforced by the API (PasswordPolicy); the client mirrors them only for early feedback. */
export const PASSWORD_MIN = 7;
export const PASSWORD_MAX = 128;

/** Group validator for a `password` / `confirm` pair. */
export const passwordsMatch = (group: AbstractControl): ValidationErrors | null =>
  group.get('password')?.value === group.get('confirm')?.value ? null : { mismatch: true };

/** Requests that send an email end in a retryable error after this long instead of an endless spinner. */
const EMAIL_REQUEST_TIMEOUT_MS = 20_000;

/** Anonymous account endpoints. Register, resend and forgot always answer 202 so they never reveal whether an account exists. */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly api = inject(Api);

  register(email: string, password: string, displayName: string): Observable<void> {
    return this.api
      .invoke(register, { body: { email, password, displayName } })
      .pipe(timeout(EMAIL_REQUEST_TIMEOUT_MS));
  }

  confirmEmail(userId: string, token: string): Observable<void> {
    return this.api.invoke(confirmEmail, { body: { userId, token } });
  }

  resendConfirmation(email: string): Observable<void> {
    return this.api
      .invoke(resendConfirmation, { body: { email } })
      .pipe(timeout(EMAIL_REQUEST_TIMEOUT_MS));
  }

  forgotPassword(email: string): Observable<void> {
    return this.api
      .invoke(forgotPassword, { body: { email } })
      .pipe(timeout(EMAIL_REQUEST_TIMEOUT_MS));
  }

  resetPassword(email: string, token: string, newPassword: string): Observable<void> {
    return this.api.invoke(resetPassword, { body: { email, token, newPassword } });
  }
}

/** Generic translation key for transport-level failures shared by the account screens. */
export function requestErrorKey(err: unknown, fallback = 'authFlow.errors.generic'): string {
  if (err instanceof TimeoutError) return 'authFlow.errors.timeout';
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return 'authFlow.errors.network';
    if (err.status === 429) return 'authFlow.errors.rateLimited';
  }
  return fallback;
}
