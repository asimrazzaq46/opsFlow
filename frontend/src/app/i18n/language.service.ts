import { Injectable, inject, signal } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

export const SUPPORTED_LANGUAGES = ['en', 'it'] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];
export const DEFAULT_LANGUAGE: Language = 'en';

const STORAGE_KEY = 'opsflow.lang';

@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly translate = inject(TranslateService);
  private readonly _current = signal<Language>(DEFAULT_LANGUAGE);
  readonly current = this._current.asReadonly();

  constructor() {
    this.set(this.initial());
  }

  set(lang: Language): void {
    this._current.set(lang);
    this.translate.use(lang);
    document.documentElement.lang = lang;
    try {
      localStorage.setItem(STORAGE_KEY, lang);
    } catch {
      /* storage unavailable */
    }
  }

  private initial(): Language {
    let stored: string | null = null;
    try {
      stored = localStorage.getItem(STORAGE_KEY);
    } catch {
      /* storage unavailable */
    }
    const candidate = stored ?? navigator.language.slice(0, 2);
    return SUPPORTED_LANGUAGES.find((l) => l === candidate) ?? DEFAULT_LANGUAGE;
  }
}
