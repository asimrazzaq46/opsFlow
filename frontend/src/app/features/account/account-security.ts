import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { UserProfileDto } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { GoogleCredential, googleErrorKey } from '../../core/auth/google-identity.service';
import { TenantService } from '../../core/auth/tenant.service';
import { GoogleSignIn } from '../auth/google-sign-in/google-sign-in';
import { AccountApi } from './account-api';
import { AccountPassword } from './account-password/account-password';
import { AccountProfile } from './account-profile/account-profile';
import { AccountSessions } from './account-sessions/account-sessions';

/**
 * Account settings: profile, password, Google linking and device sessions, all backed by `/auth/*` endpoints for the signed-in user.
 * Google linking needs a password re-authentication first; that password only lives in this form and is sent once per challenge.
 */
@Component({
  selector: 'app-account-security',
  imports: [ReactiveFormsModule, TranslatePipe, GoogleSignIn, AccountProfile, AccountPassword, AccountSessions],
  templateUrl: './account-security.html',
  styleUrl: './account-security.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountSecurity {
  private readonly account = inject(AccountApi);
  private readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  private readonly sessionList = viewChild(AccountSessions);

  protected readonly profile = rxResource({ stream: () => this.account.profile() });
  protected readonly password = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  /** Set once the user confirmed the password; renders the Google button with a link challenge. */
  protected readonly confirmedPassword = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly linked = signal(false);

  protected profileSaved(p: UserProfileDto): void {
    this.profile.set(p);
    this.auth.updateUser({ displayName: p.displayName });
  }

  protected passwordChanged(): void {
    this.sessionList()?.reload();
  }

  protected confirm(): void {
    if (this.password.invalid || this.busy()) return;
    this.error.set(null);
    this.confirmedPassword.set(this.password.value);
  }

  protected cancel(): void {
    this.confirmedPassword.set(null);
    this.password.reset();
    this.error.set(null);
  }

  protected challengeFailed(err: unknown): void {
    this.error.set(googleErrorKey(err));
    this.confirmedPassword.set(null); // a wrong password must be re-entered; nothing retries with it automatically
  }

  protected link(credential: GoogleCredential): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.account.linkGoogle(credential).subscribe({
      next: () => {
        this.busy.set(false);
        this.linked.set(true);
        this.cancel();
        this.profile.reload();
      },
      error: (err: unknown) => {
        this.error.set(googleErrorKey(err));
        this.busy.set(false);
      },
    });
  }
}
