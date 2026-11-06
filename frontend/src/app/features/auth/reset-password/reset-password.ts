import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthApi, PASSWORD_MAX, PASSWORD_MIN, passwordsMatch, requestErrorKey } from '../auth-api';
import { PasswordField } from '../ui/password-field/password-field';

type View = 'form' | 'done' | 'invalid';

/**
 * Consumes the emailed reset link. The link parameters are moved into memory and removed from the address bar; the token is
 * only judged by the API when the form is submitted (there is no separate validation endpoint), and no session is created.
 */
@Component({
  selector: 'app-reset-password',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe, PasswordField],
  templateUrl: './reset-password.html',
  styleUrl: './reset-password.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPassword implements OnInit {
  private readonly api = inject(AuthApi);
  private readonly router = inject(Router);

  readonly email = input<string | undefined>();
  readonly token = input<string | undefined>();

  private link: { email: string; token: string } | null = null;
  protected readonly address = signal<string | null>(null);
  protected readonly view = signal<View>('form');
  protected readonly min = PASSWORD_MIN;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = new FormGroup(
    {
      password: new FormControl('', {
        nonNullable: true,
        validators: [
          Validators.required,
          Validators.minLength(PASSWORD_MIN),
          Validators.maxLength(PASSWORD_MAX),
        ],
      }),
      confirm: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    },
    { validators: passwordsMatch },
  );
  private readonly value = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });
  protected readonly lengthOk = computed(
    () => (this.value().password ?? '').length >= PASSWORD_MIN,
  );
  protected readonly matchState = computed(() => {
    const { password, confirm } = this.value();
    return !confirm ? 'pending' : password === confirm ? 'match' : 'mismatch';
  });

  ngOnInit(): void {
    const email = this.email();
    const token = this.token();
    if (email && token) {
      this.link = { email, token };
      this.address.set(email);
    } else {
      this.view.set('invalid');
    }
    if (email !== undefined || token !== undefined)
      void this.router.navigate([], { queryParams: {}, replaceUrl: true });
  }

  protected showError(name: 'password' | 'confirm'): boolean {
    const c = this.form.controls[name];
    const invalid = name === 'confirm' ? c.invalid || this.form.hasError('mismatch') : c.invalid;
    return invalid && (c.touched || this.submitted());
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid || this.busy() || !this.link) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .resetPassword(this.link.email, this.link.token, this.form.controls.password.value)
      .subscribe({
        next: () => {
          this.link = null;
          this.form.reset();
          this.view.set('done');
          this.busy.set(false);
        },
        error: (err: unknown) => {
          this.busy.set(false);
          if (err instanceof HttpErrorResponse && err.status === 400) {
            // A field error means the password broke the server policy; anything else is a used, expired or forged link.
            if (err.error?.errors?.newPassword) return this.error.set('authFlow.reset.policy');
            this.link = null;
            return this.view.set('invalid');
          }
          this.error.set(requestErrorKey(err));
        },
      });
  }
}
