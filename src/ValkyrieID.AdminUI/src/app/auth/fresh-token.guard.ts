import { inject } from '@angular/core';
import { CanActivateChildFn } from '@angular/router';
import { AuthService, RENEW_BEFORE_SECONDS } from './auth.service';

/**
 * Renews the access token before a page opens when it's about to expire, so forms aren't opened with a token that
 * expires while the admin types. Renewal is a top-level `prompt=none` redirect back to the requested page.
 */
export const freshTokenGuard: CanActivateChildFn = async (_route, state) => {
  const auth = inject(AuthService);
  if (auth.secondsLeft() > RENEW_BEFORE_SECONDS) {
    return true;
  }

  await auth.signIn(state.url, true);
  return false;
};
