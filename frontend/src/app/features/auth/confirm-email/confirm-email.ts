import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { Location } from '@angular/common';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { safeReturnUrl } from '../../../core/auth/return-url';
import { AuthApi, requestErrorKey } from '../auth-api';

type View = 'inbox' | 'verifying' | 'confirmed' | 'invalid';

/**
 * Two entry points: right after registration ("check your inbox", email passed in navigation state, never in the URL) and the
 * emailed link (`userId` + single-use `token`). The token is removed from the address bar before the request so it does not
 * linger in history; success is only shown after the API confirmed it.
 */
@Component({
  selector: 'app-confirm-email',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './confirm-email.html',
  styleUrl: './confirm-email.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmEmail implements OnInit {
  private readonly api = inject(AuthApi);
  private readonly router = inject(Router);
  private readonly location = inject(Location);

  readonly userId = input<string | undefined>();
  readonly token = input<string | undefined>();
  readonly returnUrl = input<string | undefined>();
  protected readonly safeReturn = computed(() => safeReturnUrl(this.returnUrl()));

  protected readonly view = signal<View>('inbox');
  protected readonly sentTo = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly resendBusy = signal(false);
  protected readonly resendResult = signal<'sent' | string | null>(null);
  protected readonly email = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] });

  ngOnInit(): void {
    const state = this.location.getState() as { email?: unknown } | null;
    if (typeof state?.email === 'string') {
      this.sentTo.set(state.email);
      this.email.setValue(state.email);
    }

    const userId = this.userId();
    const token = this.token();
    if (userId === undefined && token === undefined) return;
    void this.router.navigate([], { queryParams: { returnUrl: this.safeReturn() }, replaceUrl: true });
    if (!userId || !token) return this.view.set('invalid');

    this.view.set('verifying');
    this.api.confirmEmail(userId, token).subscribe({
      next: () => this.view.set('confirmed'),
      error: (err: unknown) => {
        this.view.set('invalid');
        if (!(err instanceof HttpErrorResponse && err.status === 400)) this.error.set(requestErrorKey(err));
      },
    });
  }

  protected resend(): void {
    this.email.markAsTouched();
    if (this.email.invalid || this.resendBusy()) return;
    this.resendBusy.set(true);
    this.resendResult.set(null);
    this.api.resendConfirmation(this.email.value.trim()).subscribe({
      next: () => {
        this.resendResult.set('sent');
        this.resendBusy.set(false);
      },
      error: (err: unknown) => {
        this.resendResult.set(requestErrorKey(err));
        this.resendBusy.set(false);
      },
    });
  }
}
