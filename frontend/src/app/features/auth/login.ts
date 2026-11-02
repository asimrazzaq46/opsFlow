import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../core/auth/auth.service';
import { GoogleCredential, googleErrorKey } from '../../core/auth/google-identity.service';
import { TenantService } from '../../core/auth/tenant.service';
import { GoogleSignIn } from './google-sign-in/google-sign-in';

/** Password and Google sign-in. A new Google user is registered by the backend; workspace onboarding then follows the usual rules. */
@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, TranslatePipe, GoogleSignIn],
  templateUrl: './login.html',
  styleUrl: './login.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal(false);
  protected readonly googleError = signal<string | null>(null);
  protected readonly verifyEmail = signal(false);
  protected readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected submit(): void {
    if (this.form.invalid || this.busy()) return;
    this.start();
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: () => this.enter(),
      error: () => {
        this.error.set(true);
        this.busy.set(false);
      },
    });
  }

  protected signInWithGoogle(credential: GoogleCredential): void {
    if (this.busy()) return;
    this.start();
    this.auth.loginWithGoogle(credential).subscribe({
      next: (session) => {
        if (session) return this.enter();
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
    this.error.set(false);
    this.googleError.set(null);
    this.verifyEmail.set(false);
  }

  private enter(): void {
    this.tenant.load();
    void this.router.navigateByUrl('/dashboard');
  }
}
