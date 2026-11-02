import { HttpErrorResponse } from '@angular/common/http';
import { DOCUMENT, Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api } from '../api/generated/api';
import { createGoogleChallenge, createGoogleLinkChallenge } from '../api/generated/functions';
import { GoogleChallenge } from '../api/generated/models';

export const GIS_SCRIPT_URL = 'https://accounts.google.com/gsi/client';
const SCRIPT_TIMEOUT_MS = 15_000;

export type GooglePurpose = 'signIn' | 'link';

/** What the backend needs: the Google ID token and the challenge whose nonce it carries. Never stored anywhere. */
export interface GoogleCredential {
  credential: string;
  challengeId: string;
}

export interface GoogleButtonOptions {
  locale: string;
  theme: 'light' | 'dark';
  /** Only for linking: the password that re-authenticates the signed-in user when the challenge is issued. */
  password?: string;
}

export interface GoogleButtonHandle {
  id: number;
  expiresAt: number;
}

interface GoogleIdApi {
  initialize(config: Record<string, unknown>): void;
  renderButton(parent: HTMLElement, options: Record<string, unknown>): void;
  cancel(): void;
}

type GoogleWindow = Window & { google?: { accounts?: { id?: GoogleIdApi } } };

interface Registration {
  id: number;
  challengeId: string;
  expiresAt: number;
  onCredential: (credential: GoogleCredential) => void;
}

/**
 * Single owner of the Google Identity Services SDK on the page. GIS keeps ONE global configuration and the nonce is part of it, so a
 * fresh one-time challenge requires a controlled re-initialization; only the most recent registration may receive a credential and
 * each registration accepts at most one. Stale, expired or released callbacks are dropped. One Tap and auto-select are never enabled.
 */
@Injectable({ providedIn: 'root' })
export class GoogleIdentityService {
  private readonly api = inject(Api);
  private readonly document = inject(DOCUMENT);

  private scriptLoad: Promise<GoogleIdApi> | null = null;
  private active: Registration | null = null;
  private nextId = 0;

  /**
   * Requests a new challenge, (re)initializes GIS with its nonce and renders the official button into `host`.
   * Resolves null when Google sign-in is not configured on the server; rejects on network, challenge or script failures.
   */
  async render(
    host: HTMLElement,
    purpose: GooglePurpose,
    options: GoogleButtonOptions,
    onCredential: (credential: GoogleCredential) => void,
  ): Promise<GoogleButtonHandle | null> {
    let challenge: GoogleChallenge;
    try {
      challenge = await firstValueFrom(
        purpose === 'link'
          ? this.api.invoke(createGoogleLinkChallenge, { body: { password: options.password ?? '' } })
          : this.api.invoke(createGoogleChallenge, {}),
      );
    } catch (err) {
      if (err instanceof HttpErrorResponse && err.status === 404 && err.error?.code === 'AUTH_GOOGLE_DISABLED') return null;
      throw err;
    }

    const gis = await this.load();
    const registration: Registration = {
      id: ++this.nextId,
      challengeId: challenge.challengeId,
      expiresAt: new Date(challenge.expiresAtUtc).getTime(),
      onCredential,
    };
    this.active = registration;
    gis.initialize({
      client_id: challenge.clientId,
      nonce: challenge.nonce,
      callback: (response: { credential?: string }) => this.receive(registration.id, response.credential),
      auto_select: false,
      cancel_on_tap_outside: true,
      ux_mode: 'popup',
      context: purpose === 'link' ? 'use' : 'signin',
      itp_support: true,
      use_fedcm_for_button: true,
    });
    host.replaceChildren();
    gis.renderButton(host, {
      type: 'standard',
      theme: options.theme === 'dark' ? 'filled_black' : 'outline',
      size: 'large',
      text: purpose === 'link' ? 'continue_with' : 'signin_with',
      shape: 'rectangular',
      logo_alignment: 'left',
      locale: options.locale,
      width: Math.max(200, Math.min(400, host.clientWidth || 320)),
    });
    return { id: registration.id, expiresAt: registration.expiresAt };
  }

  /** Drops the registration (component destroyed or challenge abandoned); a late Google callback for it is ignored. */
  release(id: number): void {
    if (this.active?.id !== id) return;
    this.active = null;
    this.gis()?.cancel();
  }

  private receive(id: number, credential: string | undefined): void {
    const registration = this.active;
    if (!credential || registration?.id !== id || Date.now() >= registration.expiresAt) return;
    this.active = null; // one credential per challenge: a second callback can never reuse a consumed nonce
    registration.onCredential({ credential, challengeId: registration.challengeId });
  }

  private gis(): GoogleIdApi | undefined {
    return (this.document.defaultView as GoogleWindow | null)?.google?.accounts?.id;
  }

  /** Loads the official script once; a failed load is forgotten so a retry can try again. */
  private load(): Promise<GoogleIdApi> {
    const ready = this.gis();
    if (ready) return Promise.resolve(ready);
    this.scriptLoad ??= new Promise<GoogleIdApi>((resolve, reject) => {
      const script = this.document.createElement('script');
      const fail = (reason: string) => {
        clearTimeout(timer);
        script.remove();
        this.scriptLoad = null;
        reject(new Error(reason));
      };
      const timer = setTimeout(() => fail('Google Identity Services did not load in time.'), SCRIPT_TIMEOUT_MS);
      script.src = GIS_SCRIPT_URL;
      script.async = true;
      script.defer = true;
      script.onload = () => {
        const gis = this.gis();
        if (!gis) return fail('Google Identity Services is unavailable.');
        clearTimeout(timer);
        resolve(gis);
      };
      script.onerror = () => fail('Google Identity Services could not be loaded.');
      this.document.head.appendChild(script);
    });
    return this.scriptLoad;
  }
}

const GOOGLE_ERROR_KEYS: Record<string, string> = {
  AUTH_GOOGLE_LINK_REQUIRED: 'google.errors.linkRequired',
  AUTH_GOOGLE_CHALLENGE_INVALID: 'google.errors.expired',
  AUTH_GOOGLE_TOKEN_INVALID: 'google.errors.invalid',
  AUTH_GOOGLE_EMAIL_NOT_VERIFIED: 'google.errors.googleEmailUnverified',
  AUTH_EMAIL_NOT_VERIFIED: 'google.errors.emailNotVerified',
  AUTH_ACCOUNT_DISABLED: 'google.errors.disabled',
  AUTH_GOOGLE_IDENTITY_CONFLICT: 'google.errors.identityConflict',
  AUTH_GOOGLE_ALREADY_LINKED: 'google.errors.alreadyLinked',
  AUTH_PASSWORD_NOT_SET: 'google.errors.passwordNotSet',
  AUTH_GOOGLE_UNAVAILABLE: 'google.errors.unavailable',
  VALIDATION_ERROR: 'google.errors.wrongPassword',
};

/** Translation key for a failed Google sign-in/link request, based on the API's stable error code. */
export function googleErrorKey(err: unknown): string {
  if (!(err instanceof HttpErrorResponse)) return 'google.errors.generic';
  if (err.status === 429) return 'google.errors.rateLimited';
  const code: unknown = err.error?.code;
  return (typeof code === 'string' && GOOGLE_ERROR_KEYS[code]) || 'google.errors.generic';
}
