import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const ANONYMOUS = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

const withToken = (req: HttpRequest<unknown>, token: string | null) =>
  token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

/** Bearer token for same-origin API calls only; one refresh + one retry on 401. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/') || ANONYMOUS.some((u) => req.url.startsWith(u))) return next(req);

  const auth = inject(AuthService);
  const hadToken = auth.accessToken() !== null;
  return next(withToken(req, auth.accessToken())).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || !hadToken) return throwError(() => err);
      return auth.refresh().pipe(
        switchMap((r) => next(withToken(req, r.accessToken))),
        catchError(() => throwError(() => err)),
      );
    }),
  );
};
