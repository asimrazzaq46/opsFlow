import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';

/** Password input with a leading icon and an accessible show/hide toggle. The label stays with the parent for layout freedom. */
@Component({
  selector: 'app-password-field',
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './password-field.html',
  styleUrl: './password-field.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PasswordField {
  readonly control = input.required<FormControl<string>>();
  readonly inputId = input.required<string>();
  readonly autocomplete = input<'current-password' | 'new-password'>('current-password');
  readonly icon = input('lock');
  readonly placeholder = input('');
  readonly invalid = input(false);
  readonly describedBy = input<string | null>(null);

  protected readonly visible = signal(false);
}
