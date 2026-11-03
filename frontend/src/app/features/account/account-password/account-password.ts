import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../../core/auth/auth.service';
import { PASSWORD_MAX, PASSWORD_MIN, passwordsMatch, requestErrorKey } from '../../auth/auth-api';
import { PasswordField } from '../../auth/ui/password-field/password-field';

/** Changing the password revokes every session server-side; this device continues with the new token pair from the response. */
@Component({
  selector: 'app-account-password',
  imports: [ReactiveFormsModule, TranslatePipe, PasswordField],
  templateUrl: './account-password.html',
  styleUrl: './account-password.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountPassword {
  private readonly auth = inject(AuthService);
  readonly changed = output<void>();

  protected readonly min = PASSWORD_MIN;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly status = signal<'changed' | string | null>(null);
  protected readonly form = new FormGroup(
    {
      current: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
      password: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(PASSWORD_MIN), Validators.maxLength(PASSWORD_MAX)] }),
      confirm: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    },
    { validators: passwordsMatch },
  );

  protected invalid(name: 'current' | 'password' | 'confirm'): boolean {
    const c = this.form.controls[name];
    return (c.invalid || (name === 'confirm' && this.form.hasError('mismatch'))) && (c.touched || this.submitted());
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.status.set(null);
    const { current, password } = this.form.getRawValue();
    this.auth.changePassword(current, password).subscribe({
      next: () => {
        this.busy.set(false);
        this.submitted.set(false);
        this.form.reset();
        this.status.set('changed');
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const errors = err instanceof HttpErrorResponse && err.status === 400 ? err.error?.errors : null;
        this.status.set(
          errors?.currentPassword ? 'account.password.wrongCurrent' : errors?.newPassword ? 'authFlow.reset.policy' : requestErrorKey(err, 'account.password.failed'),
        );
      },
    });
  }
}
