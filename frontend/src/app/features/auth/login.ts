import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mx-auto max-w-md rounded-2xl border border-border bg-card p-6 shadow-sm sm:p-8">
      <h1 class="text-2xl font-bold tracking-tight">{{ 'auth.signIn' | translate }}</h1>
      <form class="mt-6 space-y-4" [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <div>
          <label for="login-email" class="block text-sm font-medium">{{ 'auth.email' | translate }}</label>
          <input id="login-email" type="email" autocomplete="username" formControlName="email"
            class="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-brand" />
        </div>
        <div>
          <label for="login-password" class="block text-sm font-medium">{{ 'auth.password' | translate }}</label>
          <input id="login-password" type="password" autocomplete="current-password" formControlName="password"
            class="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 focus-visible:outline-2 focus-visible:outline-brand" />
        </div>
        @if (error()) {
          <p role="alert" class="text-sm text-danger">{{ 'auth.invalid' | translate }}</p>
        }
        <button type="submit" [disabled]="form.invalid || busy()"
          class="w-full rounded-lg bg-brand px-4 py-2 font-semibold text-brand-fg disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
          {{ 'auth.submit' | translate }}
        </button>
      </form>
    </section>
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal(false);
  protected readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected submit(): void {
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(false);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: () => {
        this.tenant.load();
        void this.router.navigateByUrl('/dashboard');
      },
      error: () => {
        this.error.set(true);
        this.busy.set(false);
      },
    });
  }
}
