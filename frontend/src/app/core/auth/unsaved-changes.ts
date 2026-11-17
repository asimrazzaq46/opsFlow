import { Injectable, signal } from '@angular/core';

/** Lets a page with an unsaved form tell the shell, so the workspace switcher can warn before discarding it. */
@Injectable({ providedIn: 'root' })
export class UnsavedChanges {
  readonly dirty = signal(false);
}
