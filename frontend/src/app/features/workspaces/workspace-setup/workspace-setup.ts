import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { catchError, of, switchMap, timeout } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { requestErrorKey } from '../../auth/auth-api';
import { WorkspaceApi } from '../workspace-api';

/** Upper bound for the create request. */
const REQUEST_TIMEOUT_MS = 20_000;

/**
 * Creates a workspace. The server makes the caller its Owner and seeds default ticket settings, categories and SLA policies,
 * so nothing else is created from the client. The form stays locked after success so a double click cannot create a second one.
 */
@Component({
  selector: 'app-workspace-setup',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './workspace-setup.html',
  styleUrl: './workspace-setup.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSetup {
  private readonly api = inject(WorkspaceApi);
  private readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  protected readonly provided = [
    { key: 'owner', icon: 'shield_person' },
    { key: 'defaults', icon: 'category' },
    { key: 'sla', icon: 'timer' },
    { key: 'team', icon: 'group_add' },
  ] as const;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.minLength(2), Validators.maxLength(100)],
  });
  /** ngSubmit is emitted only by a form bound to a FormGroup; a plain form would submit natively and never call the API. */
  protected readonly form = new FormGroup({ name: this.name });

  protected submit(): void {
    this.submitted.set(true);
    const name = this.name.value.trim();
    if (this.name.invalid || name.length < 2 || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api
      .create(name)
      .pipe(
        timeout(REQUEST_TIMEOUT_MS), // an unanswered request ends in a retryable error, never an endless spinner
        // The workspace exists even if the list refresh fails; selection then falls back to the picker.
        switchMap((org) =>
          this.tenant.load().pipe(
            switchMap(() => of(org)),
            catchError(() => of(org)),
          ),
        ),
      )
      .subscribe({
        next: (org) =>
          void this.router.navigateByUrl(this.tenant.select(org.id) ? '/dashboard' : '/workspaces'),
        error: (err: unknown) => {
          this.error.set(
            err instanceof HttpErrorResponse && err.status === 400
              ? 'workspaces.setup.invalid'
              : err instanceof HttpErrorResponse && err.status === 409
                ? 'workspaces.setup.conflict'
                : requestErrorKey(err),
          );
          this.busy.set(false);
        },
      });
  }
}
