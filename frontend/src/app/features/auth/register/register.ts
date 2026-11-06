import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../../core/auth/auth.service';
import { GoogleCredential, googleErrorKey } from '../../../core/auth/google-identity.service';
import { InvitationPreviewDto } from '../../../core/api/generated/models';
import { invitationTokenFrom } from '../../../core/auth/pending-invitation';
import { PostSignIn } from '../../../core/auth/post-sign-in';
import { safeReturnUrl } from '../../../core/auth/return-url';
import { WorkspaceApi } from '../../workspaces/workspace-api';
import { AuthApi, PASSWORD_MAX, PASSWORD_MIN, passwordsMatch, requestErrorKey } from '../auth-api';
import { GoogleSignIn } from '../google-sign-in/google-sign-in';
import { FlowSteps } from '../ui/flow-steps/flow-steps';
import { PasswordField } from '../ui/password-field/password-field';

/**
 * Creates a personal account only. Organization membership comes later from creating a workspace or accepting an invitation;
 * roles are never chosen here. The API answers 202 for new and existing emails alike, so the next screen is always "check your inbox".
 * Coming from an invitation link, the invited address is shown from the read-only preview and cannot be edited, so the account that
 * will later accept is the one that was invited (the server still checks this at acceptance).
 */
@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe, GoogleSignIn, PasswordField, FlowSteps],
  templateUrl: './register.html',
  styleUrl: './register.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Register {
  private readonly api = inject(AuthApi);
  private readonly auth = inject(AuthService);
  private readonly postSignIn = inject(PostSignIn);
  private readonly router = inject(Router);
  private readonly workspaces = inject(WorkspaceApi);

  readonly returnUrl = input<string | undefined>();
  protected readonly safeReturn = computed(() => safeReturnUrl(this.returnUrl()));
  protected readonly inviteToken = computed(() => invitationTokenFrom(this.safeReturn()));
  protected readonly invite = signal<InvitationPreviewDto | null>(null);
  protected readonly min = PASSWORD_MIN;

  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly googleError = signal<string | null>(null);
  protected readonly verifyEmail = signal(false);
  protected readonly form = new FormGroup(
    {
      displayName: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.minLength(2), Validators.maxLength(100)],
      }),
      email: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.email, Validators.maxLength(256)],
      }),
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

  constructor() {
    effect(() => {
      const token = this.inviteToken();
      if (token) untracked(() => this.loadInvite(token));
    });
  }

  private loadInvite(token: string): void {
    this.workspaces.previewInvitation(token).subscribe({
      next: (preview) => {
        this.invite.set(preview);
        if (preview.state === 'Valid' && preview.email) {
          this.form.controls.email.setValue(preview.email);
          this.form.controls.email.disable();
        }
      },
      error: () => this.invite.set(null), // the form stays usable; acceptance is checked by the server later
    });
  }

  protected showError(name: 'displayName' | 'email' | 'password' | 'confirm'): boolean {
    const c = this.form.controls[name];
    const invalid = name === 'confirm' ? c.invalid || this.form.hasError('mismatch') : c.invalid;
    return invalid && (c.touched || this.submitted());
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    const { displayName, email, password } = this.form.getRawValue();
    const address = email.trim();
    this.api.register(address, password, displayName.trim()).subscribe({
      next: () =>
        void this.router.navigate(['/auth/confirm-email'], {
          queryParams: { returnUrl: this.safeReturn() },
          state: { email: address },
        }),
      error: (err: unknown) => {
        this.error.set(
          err instanceof HttpErrorResponse && err.status === 400
            ? 'authFlow.register.invalid'
            : requestErrorKey(err),
        );
        this.busy.set(false);
      },
    });
  }

  protected signUpWithGoogle(credential: GoogleCredential): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.googleError.set(null);
    this.verifyEmail.set(false);
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
}
