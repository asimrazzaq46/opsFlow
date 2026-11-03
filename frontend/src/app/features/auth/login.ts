import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../core/auth/auth.service';
import { GoogleCredential, googleErrorKey } from '../../core/auth/google-identity.service';
import { PostSignIn } from '../../core/auth/post-sign-in';
import { safeReturnUrl } from '../../core/auth/return-url';
import { requestErrorKey } from './auth-api';
import { GoogleSignIn } from './google-sign-in/google-sign-in';
import { PasswordField } from './ui/password-field/password-field';

/** Capabilities that exist in the product today; shown on the sign-in overview panel. */
const AUTH_FEATURES = [
  { key: 'tickets', icon: 'confirmation_number' },
  { key: 'sla', icon: 'timer' },
  { key: 'board', icon: 'view_kanban' },
  { key: 'workspaces', icon: 'hub' },
] as const;

/** Password and Google sign-in. A new Google user is registered by the backend; workspace onboarding then follows the usual rules. */
@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe, GoogleSignIn, PasswordField],
  templateUrl: './login.html',
  styleUrl: './login.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly postSignIn = inject(PostSignIn);

  /** Bound from the query string; only an in-app path is ever used. */
  readonly returnUrl = input<string | undefined>();
  protected readonly safeReturn = computed(() => safeReturnUrl(this.returnUrl()));

  protected readonly features = AUTH_FEATURES;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly googleError = signal<string | null>(null);
  protected readonly verifyEmail = signal(false);
  protected readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected showError(name: 'email' | 'password'): boolean {
    const c = this.form.controls[name];
    return c.invalid && (c.touched || this.submitted());
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid || this.busy()) return;
    this.start();
    const { email, password } = this.form.getRawValue();
    this.auth.login(email.trim(), password).subscribe({
      next: () => this.postSignIn.enter(this.safeReturn()),
      error: (err: unknown) => {
        // 400/401 are deliberately one generic message: the API never says which part was wrong.
        this.error.set(err instanceof HttpErrorResponse && (err.status === 400 || err.status === 401) ? 'auth.invalid' : requestErrorKey(err));
        this.busy.set(false);
      },
    });
  }

  protected signInWithGoogle(credential: GoogleCredential): void {
    if (this.busy()) return;
    this.start();
    this.auth.loginWithGoogle(credential).subscribe({
      next: (session) => {
        if (session) return this.postSignIn.enter(this.safeReturn());
        this.verifyEmail.set(true);
        this.busy.set(false);
      },
      error: (err: unknown) => {
        this.googleError.set(googleErrorKey(err));
        this.busy.set(false);
      },
    });
  }

  private start(): void {
    this.busy.set(true);
    this.error.set(null);
    this.googleError.set(null);
    this.verifyEmail.set(false);
  }
}
