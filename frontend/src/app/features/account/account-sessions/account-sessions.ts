import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslatePipe } from '@ngx-translate/core';
import { SessionResponse } from '../../../core/api/generated/models';
import { AuthService } from '../../../core/auth/auth.service';
import { LanguageService } from '../../../i18n/language.service';
import { requestErrorKey } from '../../auth/auth-api';
import { AccountApi } from '../account-api';

/** Short "Browser · OS" label; the raw user agent stays available as a tooltip. */
export function deviceLabel(userAgent: string | null | undefined): string | null {
  if (!userAgent) return null;
  const browser = /Edg\//.test(userAgent) ? 'Edge' : /Firefox\//.test(userAgent) ? 'Firefox' : /Chrome\//.test(userAgent) ? 'Chrome' : /Safari\//.test(userAgent) ? 'Safari' : null;
  const os = /Windows/.test(userAgent) ? 'Windows' : /Android/.test(userAgent) ? 'Android' : /iPhone|iPad/.test(userAgent) ? 'iOS' : /Mac OS/.test(userAgent) ? 'macOS' : /Linux/.test(userAgent) ? 'Linux' : null;
  return [browser, os].filter(Boolean).join(' · ') || null;
}

/** Sessions are the user's refresh-token families; revoking the current one or all of them ends this session locally too. */
@Component({
  selector: 'app-account-sessions',
  imports: [TranslatePipe],
  templateUrl: './account-sessions.html',
  styleUrl: './account-sessions.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountSessions {
  private readonly account = inject(AccountApi);
  private readonly auth = inject(AuthService);
  private readonly language = inject(LanguageService);

  protected readonly sessions = rxResource({ stream: () => this.account.sessions() });
  protected readonly busyId = signal<string | null>(null);
  protected readonly confirmAll = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly deviceLabel = deviceLabel;

  reload(): void {
    this.sessions.reload();
  }

  protected when(iso: string): string {
    return new Date(iso).toLocaleString(this.language.current(), { dateStyle: 'medium', timeStyle: 'short' });
  }

  protected revoke(s: SessionResponse): void {
    if (this.busyId()) return;
    if (s.isCurrent) return this.auth.logout();
    this.busyId.set(s.id);
    this.error.set(null);
    this.account.revokeSession(s.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.sessions.update((list) => list?.filter((x) => x.id !== s.id));
      },
      error: (err: unknown) => {
        this.busyId.set(null);
        this.error.set(requestErrorKey(err, 'account.sessions.revokeError'));
        this.sessions.reload();
      },
    });
  }

  protected logoutAll(): void {
    if (this.busyId()) return;
    this.busyId.set('all');
    this.error.set(null);
    this.account.logoutAll().subscribe({
      // The current session is revoked as well, so only local cleanup is left.
      next: () => this.auth.clear(),
      error: (err: unknown) => {
        this.busyId.set(null);
        this.confirmAll.set(false);
        this.error.set(requestErrorKey(err, 'account.sessions.revokeError'));
      },
    });
  }
}
