import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { tap } from 'rxjs';
import { AuthService } from './auth.service';
import { APP_SETTINGS } from './settings';

/**
 * Adds the access token to Admin API requests only (RFC 6750 §2.1, header only). Any other URL, including other
 * paths on the same origin, never gets it, so the token can't leak to another endpoint (RFC 9700 §4.9).
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const { apiBase } = inject(APP_SETTINGS);
  const token = auth.accessToken();
  if (!request.url.startsWith(apiBase) || token === null) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })).pipe(
    tap({
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse) {
          if (error.status === 401) {
            // RFC 9470 §3: the token is valid, but the sign-in behind it didn't use a second factor.
            if (
              error.headers.get('WWW-Authenticate')?.includes('insufficient_user_authentication')
            ) {
              auth.secondFactorRequired.set(true);
            } else {
              auth.sessionExpired.set(true);
            }
          } else if (error.status === 403) {
            auth.forbidden.set(true);
          }
        }
      },
    }),
  );
};
