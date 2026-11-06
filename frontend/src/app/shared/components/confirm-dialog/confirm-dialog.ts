import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChild,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';

let nextId = 0;

/**
 * Modal confirmation on the native dialog element: focus is trapped, Escape cancels and focus returns to the opener.
 * The parent calls `open()`; the dialog itself never performs the action.
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [TranslatePipe],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  readonly title = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input.required<string>();
  readonly busy = input(false);
  readonly confirmDisabled = input(false);
  readonly confirmed = output<void>();
  /** Emits whenever the dialog closes (cancel, Escape or programmatic). */
  readonly closed = output<void>();
  protected readonly uid = `confirm-dialog-${nextId++}`;

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  open(): void {
    const el = this.dialog().nativeElement;
    if (!el.open) el.showModal();
  }

  close(): void {
    const el = this.dialog().nativeElement;
    if (el.open) el.close();
  }
}
