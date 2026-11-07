import { inject } from '@angular/core';
import { CanActivateChildFn } from '@angular/router';
import { AuthService, RENEW_BEFORE_SECONDS } from './auth.service';

/**
 * Renews the access token before a page opens when it's about to expire, so forms aren't opened with a token that
 * expires while the admin types. Renewal is a top-level `prompt=none` redirect back to the requested page. Only while
 * signed in: the signed-out and sign-in error pages must stay on screen until the user acts.
 */
export const freshTokenGuard: CanActivateChildFn = async (_route, state) => {
  const auth = inject(AuthService);
  if (auth.status() !== 'signed-in' || auth.secondsLeft() > RENEW_BEFORE_SECONDS) {
    return true;
  }

  await auth.signIn(state.url, true);
  return false;
};
