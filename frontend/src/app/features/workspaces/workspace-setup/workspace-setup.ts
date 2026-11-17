import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { catchError, of, switchMap, timeout } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { requestErrorKey } from '../../auth/auth-api';
import { WorkspaceApi } from '../workspace-api';

/** Upper bound for the create request. */
const REQUEST_TIMEOUT_MS = 20_000;
/** Mirrors the server's default prefix and identifier rules; the server validates again. */
const DEFAULT_PREFIX = 'TKT';
const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const PREFIX_PATTERN = /^[A-Za-z][A-Za-z0-9]{1,9}$/;

/** URL-friendly suggestion from the workspace name; empty when nothing usable remains (the server then derives one). */
export function suggestSlug(name: string): string {
  return name
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .slice(0, 50)
    .replace(/^-+|-+$/g, '');
}

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
  protected readonly slug = new FormControl('', {
    nonNullable: true,
    validators: [
      (c) => {
        const v = c.value.trim();
        return v === '' || (v.length >= 2 && v.length <= 50 && SLUG_PATTERN.test(v)) ? null : { slug: true };
      },
    ],
  });
  protected readonly prefix = new FormControl(DEFAULT_PREFIX, {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(PREFIX_PATTERN)],
  });
  /** Server-side field errors (400 / 409), shown inline and cleared when the field changes. */
  protected readonly serverErrors = signal<{ slug?: string; prefix?: string }>({});
  /** Once the user has typed a slug it is never replaced by a suggestion again, unless they clear the field. */
  private slugCustomized = false;
  /** ngSubmit is emitted only by a form bound to a FormGroup; a plain form would submit natively and never call the API. */
  protected readonly form = new FormGroup({ name: this.name, slug: this.slug, prefix: this.prefix });

  constructor() {
    this.name.valueChanges.pipe(takeUntilDestroyed()).subscribe((v) => {
      if (!this.slugCustomized) this.slug.setValue(suggestSlug(v), { emitEvent: false });
    });
  }

  protected slugEdited(): void {
    this.slugCustomized = this.slug.value.trim() !== '';
    if (!this.slugCustomized) this.slug.setValue(suggestSlug(this.name.value), { emitEvent: false });
    this.serverErrors.update((e) => ({ ...e, slug: undefined }));
  }

  /** Ticket numbers are upper-case; the server normalizes too. */
  protected prefixEdited(): void {
    const v = this.prefix.value.toUpperCase();
    if (v !== this.prefix.value) this.prefix.setValue(v, { emitEvent: false });
    this.serverErrors.update((e) => ({ ...e, prefix: undefined }));
  }

  /** Maps a failed create to a field error where the server named one; returns the form-level message key otherwise. */
  private failure(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 409 && err.error?.code === 'SLUG_TAKEN') {
        this.serverErrors.set({ slug: 'workspaces.setup.slugTaken' });
        return '';
      }
      if (err.status === 400) {
        // Key casing differs between model-binding and service validation errors.
        const keys = Object.keys((err.error?.errors ?? {}) as object).map((k) => k.toLowerCase());
        if (keys.includes('slug') || keys.includes('ticketnumberprefix')) {
          this.serverErrors.set({
            slug: keys.includes('slug') ? 'workspaces.setup.slugInvalid' : undefined,
            prefix: keys.includes('ticketnumberprefix') ? 'workspaces.setup.prefixInvalid' : undefined,
          });
          return '';
        }
        return 'workspaces.setup.invalid';
      }
      if (err.status === 409) return 'workspaces.setup.conflict';
    }
    return requestErrorKey(err);
  }

  protected submit(): void {
    this.submitted.set(true);
    const name = this.name.value.trim();
    if (this.form.invalid || name.length < 2 || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.serverErrors.set({});
    this.api
      .create(name, this.slug.value.trim() || undefined, this.prefix.value.trim().toUpperCase())
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
          this.error.set(this.failure(err));
          this.busy.set(false);
        },
      });
  }
}
