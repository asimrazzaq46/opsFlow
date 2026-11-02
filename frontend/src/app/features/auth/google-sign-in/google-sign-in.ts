import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, effect, inject, input, output, signal, untracked, viewChild,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { GoogleButtonHandle, GoogleCredential, GoogleIdentityService, GooglePurpose } from '../../../core/auth/google-identity.service';
import { ThemeService } from '../../../core/services/theme.service';
import { LanguageService } from '../../../i18n/language.service';

/** Renew the challenge shortly before it expires so a click never sends a nonce the server already discarded. */
const EXPIRY_MARGIN_MS = 20_000;

type ButtonState = 'loading' | 'ready' | 'disabled' | 'error';

/**
 * Official Google button backed by a fresh one-time challenge. Every challenge is used at most once: after a credential, an expiry or
 * an error a new one is requested, but only while the parent is not busy, so a running request is never raced by a second one.
 * Renders nothing when Google sign-in is not configured.
 */
@Component({
  selector: 'app-google-sign-in',
  imports: [TranslatePipe],
  templateUrl: './google-sign-in.html',
  styleUrl: './google-sign-in.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GoogleSignIn {
  private readonly google = inject(GoogleIdentityService);
  private readonly language = inject(LanguageService);
  private readonly theme = inject(ThemeService);

  readonly purpose = input<GooglePurpose>('signIn');
  /** Linking only: the password that re-authenticates the user when the challenge is issued. */
  readonly password = input<string | null>(null);
  readonly busy = input(false);
  readonly showDivider = input(false);
  readonly credential = output<GoogleCredential>();
  /** The challenge could not be prepared (e.g. wrong password while linking); the parent decides what to show. */
  readonly prepareFailed = output<unknown>();

  protected readonly state = signal<ButtonState>('loading');
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly viewReady = signal(false);
  private readonly needsChallenge = signal(true);
  private handle: GoogleButtonHandle | null = null;
  private expiryTimer: ReturnType<typeof setTimeout> | undefined;
  private generation = 0;
  private destroyed = false;

  constructor() {
    afterNextRender(() => this.viewReady.set(true));
    effect(() => {
      if (this.viewReady() && this.needsChallenge() && !this.busy()) untracked(() => void this.prepare());
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.dispose();
    });
  }

  protected retry(): void {
    this.needsChallenge.set(true);
  }

  private async prepare(): Promise<void> {
    this.needsChallenge.set(false);
    this.dispose();
    const generation = ++this.generation;
    this.state.set('loading');
    try {
      const handle = await this.google.render(
        this.host().nativeElement,
        this.purpose(),
        { locale: this.language.current(), theme: this.theme.theme(), password: this.password() ?? undefined },
        (c) => this.receive(c),
      );
      if (this.destroyed || generation !== this.generation) {
        if (handle) this.google.release(handle.id);
        return;
      }
      if (!handle) {
        this.state.set('disabled');
        return;
      }
      this.handle = handle;
      this.state.set('ready');
      this.expiryTimer = setTimeout(() => this.needsChallenge.set(true), Math.max(0, handle.expiresAt - Date.now() - EXPIRY_MARGIN_MS));
    } catch (err) {
      if (this.destroyed || generation !== this.generation) return;
      this.state.set('error');
      this.prepareFailed.emit(err);
    }
  }

  private receive(credential: GoogleCredential): void {
    clearTimeout(this.expiryTimer);
    this.handle = null;
    // Emitted first so the parent marks itself busy before the next challenge is considered.
    this.credential.emit(credential);
    this.needsChallenge.set(true);
  }

  private dispose(): void {
    clearTimeout(this.expiryTimer);
    if (this.handle) this.google.release(this.handle.id);
    this.handle = null;
  }
}
