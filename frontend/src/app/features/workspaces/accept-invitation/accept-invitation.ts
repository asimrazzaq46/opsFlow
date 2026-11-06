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
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { catchError, map, of, switchMap } from 'rxjs';
import { InvitationPreviewDto, OrganizationDto } from '../../../core/api/generated/models';
import { AuthService } from '../../../core/auth/auth.service';
import { PendingInvitation, invitationUrl } from '../../../core/auth/pending-invitation';
import { TenantService } from '../../../core/auth/tenant.service';
import { requestErrorKey } from '../../auth/auth-api';
import { WorkspaceApi, initials } from '../workspace-api';

/**
 * Shows what the token holder may know (workspace, role, invited email) from the read-only preview. Acceptance always needs an
 * explicit click by a signed-in user; the server checks the token, expiry and that the user's verified email is the invited one, and
 * assigns the role. The preview is only a courtesy: a different signed-in account is told so before it can try. Signed-out visitors
 * keep the link through sign-in/registration via a validated `returnUrl` and the short-lived pending invitation.
 */
@Component({
  selector: 'app-accept-invitation',
  imports: [RouterLink, TranslatePipe],
  templateUrl: './accept-invitation.html',
  styleUrl: './accept-invitation.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AcceptInvitation {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(WorkspaceApi);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);
  private readonly pending = inject(PendingInvitation);

  readonly token = input<string | undefined>();
  protected readonly returnUrl = computed(() => invitationUrl(this.token() ?? ''));
  /** Null while loading or when the preview could not be fetched (acceptance is still possible; the server decides). */
  protected readonly preview = signal<InvitationPreviewDto | null>(null);
  protected readonly details = computed(() =>
    this.preview()?.state === 'Valid' ? this.preview() : null,
  );
  protected readonly unusable = computed(() => {
    const state = this.preview()?.state;
    return state === 'Expired' || state === 'Unavailable' ? state : null;
  });
  /** The signed-in account is not the invited address. The server enforces this independently. */
  protected readonly mismatch = computed(() => {
    const invited = this.details()?.email;
    const me = this.auth.user()?.email;
    return !!invited && !!me && invited.trim().toLowerCase() !== me.trim().toLowerCase();
  });
  protected readonly initials = initials;

  protected readonly busy = signal(false);
  protected readonly joined = signal<OrganizationDto | null>(null);
  protected readonly error = signal<{ title: string; text: string } | null>(null);

  constructor() {
    effect(() => {
      const token = this.token();
      if (token) untracked(() => this.loadPreview(token));
    });
  }

  private loadPreview(token: string): void {
    this.preview.set(null);
    this.api.previewInvitation(token).subscribe({
      next: (preview) => {
        this.preview.set(preview);
        if (preview.state === 'Valid') this.pending.set(token, preview.expiresAtUtc);
        else this.pending.clear(token);
      },
      error: () => this.preview.set(null),
    });
  }

  protected accept(): void {
    const token = this.token();
    if (!token || this.busy() || this.joined() || this.mismatch()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .acceptInvitation(token)
      .pipe(
        switchMap((org) =>
          this.tenant.load().pipe(
            map(() => org),
            catchError(() => of(org)),
          ),
        ),
      )
      .subscribe({
        next: (org) => {
          this.pending.clear(token);
          this.joined.set(org);
          this.busy.set(false);
        },
        error: (err: unknown) => {
          if (err instanceof HttpErrorResponse && err.status === 400) this.pending.clear(token); // unusable for everyone
          this.error.set(this.describe(err));
          this.busy.set(false);
        },
      });
  }

  protected open(): void {
    const org = this.joined();
    if (org && this.tenant.select(org.id)) void this.router.navigateByUrl('/dashboard');
    else void this.router.navigateByUrl('/workspaces');
  }

  /** Signs out so the invited address can sign in; the invitation link is kept as the return target. */
  protected switchAccount(): void {
    const target = this.returnUrl();
    this.auth.logout(false);
    void this.router.navigate(['/login'], { queryParams: { returnUrl: target } });
  }

  private describe(err: unknown): { title: string; text: string } {
    const status = err instanceof HttpErrorResponse ? err.status : -1;
    if (status === 403)
      return { title: 'invitations.accept.mismatchTitle', text: 'invitations.accept.mismatchText' };
    if (status === 400)
      return { title: 'invitations.accept.invalidTitle', text: 'invitations.accept.invalidText' };
    if (status === 409)
      return { title: 'invitations.accept.conflictTitle', text: 'invitations.accept.conflictText' };
    return { title: 'invitations.accept.failedTitle', text: requestErrorKey(err) };
  }
}
