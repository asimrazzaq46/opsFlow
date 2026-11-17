import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { timeout } from 'rxjs';
import { TenantRole } from '../../../core/api/generated/models';
import { InvitationsService } from '../../../core/api/generated/services/invitations.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { invitationErrorKey } from './invitation-errors';

/** A request that has not answered by then ends in a retryable error instead of an endless spinner. */
const REQUEST_TIMEOUT_MS = 20_000;

/**
 * Modal form that creates one invitation. The server authorizes every request; the role list only mirrors its matrix (Owners invite any
 * role, Managers only Agents and Customers). The dialog closes on success and keeps its values after a recoverable error.
 */
@Component({
  selector: 'app-invite-member-dialog',
  imports: [ReactiveFormsModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './invite-member-dialog.html',
  styleUrl: './invite-member-dialog.css',
})
export class InviteMemberDialog {
  /** Emitted after the server accepted the invitation. */
  readonly created = output<void>();

  private readonly api = inject(InvitationsService);
  protected readonly tenant = inject(TenantService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly roles = computed<TenantRole[]>(() =>
    this.tenant.isOwner() ? ['Manager', 'Agent', 'Customer', 'TenantOwner'] : ['Agent', 'Customer'],
  );
  protected readonly invite = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    role: new FormControl<TenantRole>('Agent', { nonNullable: true }),
  });
  protected readonly creating = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  constructor() {
    // A workspace switch discards the draft and closes the dialog.
    effect(() => {
      this.tenant.tenantId();
      untracked(() => {
        this.close();
        this.invite.reset({ email: '', role: 'Agent' });
        this.errorKey.set(null);
        this.creating.set(false);
      });
    });
  }

  open(): void {
    if (!this.tenant.isManager()) return;
    this.errorKey.set(null);
    const el = this.dialog().nativeElement;
    if (!el.open) el.showModal();
  }

  protected close(): void {
    const el = this.dialog().nativeElement;
    if (el.open) el.close();
  }

  /** Escape must not close the dialog while a request is in flight. */
  protected onCancel(event: Event): void {
    if (this.creating()) event.preventDefault();
  }

  protected send(): void {
    this.invite.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (!this.tenant.isManager() || this.invite.invalid || !tenantId || this.creating()) return;
    this.creating.set(true);
    this.errorKey.set(null);
    this.api
      .createInvitation({ tenantId, body: this.invite.getRawValue() })
      .pipe(timeout(REQUEST_TIMEOUT_MS))
      .subscribe({
        next: () => {
          this.creating.set(false);
          if (tenantId !== this.tenant.tenantId()) return;
          this.invite.reset({ email: '', role: this.invite.controls.role.value });
          this.close();
          this.created.emit();
        },
        error: (e: unknown) => {
          this.creating.set(false);
          if (tenantId === this.tenant.tenantId()) this.errorKey.set(invitationErrorKey(e, 'create')); // the form keeps its values
        },
      });
  }
}
