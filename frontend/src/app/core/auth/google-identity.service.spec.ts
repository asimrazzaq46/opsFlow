import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { createGoogleChallenge, createGoogleLinkChallenge } from '../api/generated/functions';
import { GoogleChallenge } from '../api/generated/models';
import { GIS_SCRIPT_URL, GoogleIdentityService, googleErrorKey } from './google-identity.service';

type GoogleWindow = Window & { google?: unknown };

const challenge = (n: number, expiresInMs = 300_000): GoogleChallenge => ({
  challengeId: `challenge-${n}`,
  nonce: `nonce-${n}`,
  expiresAtUtc: new Date(Date.now() + expiresInMs).toISOString(),
  clientId: '123-test.apps.googleusercontent.com',
});

describe('GoogleIdentityService', () => {
  const invoke = vi.fn();
  const gis = { initialize: vi.fn(), renderButton: vi.fn(), cancel: vi.fn() };
  let service: GoogleIdentityService;
  let host: HTMLElement;

  const callbackOf = (call: number) => (gis.initialize.mock.calls[call][0] as { callback: (r: { credential?: string }) => void }).callback;

  beforeEach(() => {
    vi.clearAllMocks();
    invoke.mockReset();
    (window as GoogleWindow).google = { accounts: { id: gis } };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: { invoke } }] });
    service = TestBed.inject(GoogleIdentityService);
    host = document.createElement('div');
  });

  afterEach(() => {
    delete (window as GoogleWindow).google;
    document.head.querySelectorAll(`script[src="${GIS_SCRIPT_URL}"]`).forEach((s) => s.remove());
  });

  it('initializes the SDK with the challenge nonce and never enables auto-select or One Tap', async () => {
    invoke.mockReturnValueOnce(of(challenge(1)));
    const handle = await service.render(host, 'signIn', { locale: 'it' }, vi.fn());

    expect(handle).not.toBeNull();
    expect(invoke.mock.calls[0][0]).toBe(createGoogleChallenge);
    const config = gis.initialize.mock.calls[0][0];
    expect(config).toMatchObject({ client_id: '123-test.apps.googleusercontent.com', nonce: 'nonce-1', auto_select: false, ux_mode: 'popup' });
    expect(gis.renderButton).toHaveBeenCalledWith(host, expect.objectContaining({ locale: 'it', theme: 'filled_blue', text: 'continue_with' }));
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it('hides Google (null) when the server reports it disabled, without loading the script', async () => {
    delete (window as GoogleWindow).google;
    invoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 404, error: { code: 'AUTH_GOOGLE_DISABLED' } })));

    expect(await service.render(host, 'signIn', { locale: 'en' }, vi.fn())).toBeNull();
    expect(document.head.querySelector(`script[src="${GIS_SCRIPT_URL}"]`)).toBeNull();
  });

  it('delivers exactly one credential per challenge and drops stale or released callbacks', async () => {
    const first = vi.fn();
    const second = vi.fn();
    invoke.mockReturnValueOnce(of(challenge(1))).mockReturnValueOnce(of(challenge(2)));
    await service.render(host, 'signIn', { locale: 'en' }, first);
    const handle = await service.render(host, 'signIn', { locale: 'en' }, second);

    callbackOf(0)({ credential: 'stale-token' }); // superseded registration
    expect(first).not.toHaveBeenCalled();

    callbackOf(1)({ credential: 'id-token' });
    callbackOf(1)({ credential: 'id-token-again' }); // the nonce is consumed: never forwarded twice
    expect(second).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledWith({ credential: 'id-token', challengeId: 'challenge-2' });

    invoke.mockReturnValueOnce(of(challenge(3)));
    const third = vi.fn();
    const released = await service.render(host, 'signIn', { locale: 'en' }, third);
    service.release(released!.id);
    callbackOf(2)({ credential: 'late' });
    expect(third).not.toHaveBeenCalled();
    expect(gis.cancel).toHaveBeenCalled();
    expect(handle).not.toBeNull();
  });

  it('ignores a credential that arrives after the challenge expired', async () => {
    const onCredential = vi.fn();
    invoke.mockReturnValueOnce(of(challenge(1, -1)));
    await service.render(host, 'signIn', { locale: 'en' }, onCredential);
    callbackOf(0)({ credential: 'too-late' });
    expect(onCredential).not.toHaveBeenCalled();
  });

  it('requests a link challenge with the password for account linking', async () => {
    invoke.mockReturnValueOnce(of(challenge(1)));
    await service.render(host, 'link', { locale: 'en', password: 'pw' }, vi.fn());
    expect(invoke.mock.calls[0][0]).toBe(createGoogleLinkChallenge);
    expect(invoke.mock.calls[0][1]).toEqual({ body: { password: 'pw' } });
    expect(gis.initialize.mock.calls[0][0]).toMatchObject({ context: 'use' });
  });

  it('loads the official script once for concurrent renders and allows a retry after a load failure', async () => {
    delete (window as GoogleWindow).google;
    invoke.mockImplementation(() => of(challenge(1)));

    const failing = service.render(host, 'signIn', { locale: 'en' }, vi.fn());
    await Promise.resolve();
    await Promise.resolve();
    const script = document.head.querySelector<HTMLScriptElement>(`script[src="${GIS_SCRIPT_URL}"]`)!;
    script.onerror!(new Event('error'));
    await expect(failing).rejects.toThrow();
    expect(document.head.querySelectorAll(`script[src="${GIS_SCRIPT_URL}"]`).length).toBe(0);

    const a = service.render(host, 'signIn', { locale: 'en' }, vi.fn());
    const b = service.render(host, 'signIn', { locale: 'en' }, vi.fn());
    await Promise.resolve();
    await Promise.resolve();
    const scripts = document.head.querySelectorAll<HTMLScriptElement>(`script[src="${GIS_SCRIPT_URL}"]`);
    expect(scripts.length).toBe(1);
    (window as GoogleWindow).google = { accounts: { id: gis } };
    scripts[0].onload!(new Event('load'));
    await Promise.all([a, b]);
    expect(gis.renderButton).toHaveBeenCalledTimes(2);
  });

  it('maps API error codes to translation keys', () => {
    const err = (status: number, code?: string) => new HttpErrorResponse({ status, error: code ? { code } : null });
    expect(googleErrorKey(err(409, 'AUTH_GOOGLE_LINK_REQUIRED'))).toBe('google.errors.linkRequired');
    expect(googleErrorKey(err(400, 'AUTH_GOOGLE_CHALLENGE_INVALID'))).toBe('google.errors.expired');
    expect(googleErrorKey(err(429))).toBe('google.errors.rateLimited');
    expect(googleErrorKey(err(500, 'INTERNAL_ERROR'))).toBe('google.errors.generic');
    expect(googleErrorKey(new Error('popup'))).toBe('google.errors.generic');
  });
});
