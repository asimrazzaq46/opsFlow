import { Injectable, effect, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'opsflow.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly _theme = signal<Theme>(this.initial());
  readonly theme = this._theme.asReadonly();

  constructor() {
    effect(() => {
      const theme = this._theme();
      document.documentElement.dataset['theme'] = theme;
      try {
        localStorage.setItem(STORAGE_KEY, theme);
      } catch {
        /* storage unavailable */
      }
    });
  }

  toggle(): void {
    this._theme.update((t) => (t === 'dark' ? 'light' : 'dark'));
  }

  private initial(): Theme {
    const current = document.documentElement.dataset['theme'];
    return current === 'dark' ? 'dark' : 'light';
  }
}
