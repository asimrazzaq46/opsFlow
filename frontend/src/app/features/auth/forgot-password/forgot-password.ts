import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthApi, requestErrorKey } from '../auth-api';

/** The API answers 202 for every address, so the confirmation text never states that an account exists or that mail was delivered. */
@Component({
  selector: 'app-forgot-password',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './forgot-password.html',
  styleUrl: './forgot-password.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPassword {
  private readonly api = inject(AuthApi);

  protected readonly policies = [
    { key: 'eligible', icon: 'mark_email_read' },
    { key: 'single', icon: 'lock_clock' },
    { key: 'sessions', icon: 'devices' },
  ] as const;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly accepted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly email = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] });

  protected submit(): void {
    this.submitted.set(true);
    if (this.email.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.forgotPassword(this.email.value.trim()).subscribe({
      next: () => {
        this.accepted.set(true);
        this.busy.set(false);
      },
      error: (err: unknown) => {
        this.error.set(requestErrorKey(err));
        this.busy.set(false);
      },
    });
  }

  protected again(): void {
    this.accepted.set(false);
    this.submitted.set(false);
  }
}
