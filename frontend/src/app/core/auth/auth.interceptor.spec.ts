import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

describe('authInterceptor', () => {
  const auth = { accessToken: vi.fn<() => string | null>(), refresh: vi.fn() };
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    vi.clearAllMocks();
    auth.accessToken.mockReturnValue('access-1');
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), { provide: AuthService, useValue: auth }],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('sends the access token to same-origin API calls only', () => {
    http.get('/api/v1/tickets').subscribe();
    expect(controller.expectOne('/api/v1/tickets').request.headers.get('Authorization')).toBe('Bearer access-1');

    http.get('https://elsewhere.example/api/v1/tickets').subscribe();
    expect(controller.expectOne('https://elsewhere.example/api/v1/tickets').request.headers.has('Authorization')).toBe(false);
  });

  it.each([403, 404, 429, 500, 503])('passes HTTP %i through: no refresh, no sign-out', (status) => {
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/tickets').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    controller.expectOne('/api/v1/tickets').flush({}, { status, statusText: 'x' });

    expect(error?.status).toBe(status);
    expect(auth.refresh).not.toHaveBeenCalled();
  });

  it('passes a network failure through without refreshing', () => {
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/tickets').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    controller.expectOne('/api/v1/tickets').error(new ProgressEvent('error'));

    expect(error?.status).toBe(0);
    expect(auth.refresh).not.toHaveBeenCalled();
  });

  it('refreshes once on 401 and retries the request once with the new token', () => {
    auth.refresh.mockReturnValue(of({ accessToken: 'access-2' }));
    let result: unknown;
    http.get('/api/v1/tickets').subscribe((r) => (result = r));

    controller.expectOne('/api/v1/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });
    const retry = controller.expectOne('/api/v1/tickets');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush({ ok: true });

    expect(result).toEqual({ ok: true });
    expect(auth.refresh).toHaveBeenCalledOnce();
  });

  it('does not loop: a 401 after the refresh is returned to the caller', () => {
    auth.refresh.mockReturnValue(of({ accessToken: 'access-2' }));
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/tickets').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    controller.expectOne('/api/v1/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });
    controller.expectOne('/api/v1/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(error?.status).toBe(401);
    expect(auth.refresh).toHaveBeenCalledOnce();
  });

  it('returns the original 401 when the refresh fails (signing out is the auth service decision, not the interceptor)', () => {
    auth.refresh.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 503 })));
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/tickets').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    controller.expectOne('/api/v1/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(error?.status).toBe(401);
    expect(auth.refresh).toHaveBeenCalledOnce();
  });

  it('never refreshes for the anonymous auth endpoints or when nobody is signed in', () => {
    http.post('/api/v1/auth/login', {}).subscribe({ error: () => undefined });
    controller.expectOne('/api/v1/auth/login').flush({}, { status: 401, statusText: 'Unauthorized' });

    auth.accessToken.mockReturnValue(null);
    http.get('/api/v1/tickets').subscribe({ error: () => undefined });
    controller.expectOne('/api/v1/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.refresh).not.toHaveBeenCalled();
  });
});
