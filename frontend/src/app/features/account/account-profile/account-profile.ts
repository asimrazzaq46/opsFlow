import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { UserProfileDto } from '../../../core/api/generated/models';
import { requestErrorKey } from '../../auth/auth-api';
import { AccountApi } from '../account-api';

/** Display name editing (the only profile field the API lets a user change); email is shown read-only. */
@Component({
  selector: 'app-account-profile',
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './account-profile.html',
  styleUrl: './account-profile.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountProfile {
  private readonly account = inject(AccountApi);

  readonly profile = input.required<UserProfileDto>();
  readonly saved = output<UserProfileDto>();

  protected readonly busy = signal(false);
  protected readonly status = signal<'saved' | string | null>(null);
  protected readonly name = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.minLength(2), Validators.maxLength(100)],
  });

  constructor() {
    effect(() => this.name.reset(this.profile().displayName));
  }

  protected save(): void {
    this.name.markAsTouched();
    const value = this.name.value.trim();
    if (
      this.name.invalid ||
      value.length < 2 ||
      this.busy() ||
      value === this.profile().displayName
    )
      return;
    this.busy.set(true);
    this.status.set(null);
    this.account.updateDisplayName(value).subscribe({
      next: (p) => {
        this.busy.set(false);
        this.status.set('saved');
        this.saved.emit(p);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.status.set(requestErrorKey(err, 'account.profile.saveError'));
      },
    });
  }
}
