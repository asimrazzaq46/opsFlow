import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { GoogleCredential, googleErrorKey } from '../../core/auth/google-identity.service';
import { GoogleSignIn } from '../auth/google-sign-in/google-sign-in';
import { AccountApi } from './account-api';

/**
 * Minimal sign-in methods page: shows whether Google is linked and links it after a password re-authentication.
 * The password only lives in this form while linking and is sent once per challenge.
 */
@Component({
  selector: 'app-account-security',
  imports: [ReactiveFormsModule, TranslatePipe, GoogleSignIn],
  templateUrl: './account-security.html',
  styleUrl: './account-security.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountSecurity {
  private readonly account = inject(AccountApi);

  protected readonly profile = rxResource({ stream: () => this.account.profile() });
  protected readonly password = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  /** Set once the user confirmed the password; renders the Google button with a link challenge. */
  protected readonly confirmedPassword = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly linked = signal(false);

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
