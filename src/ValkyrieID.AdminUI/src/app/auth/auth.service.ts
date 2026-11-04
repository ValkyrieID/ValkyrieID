import { Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import {
  ErrorResponse,
  InMemoryWebStorage,
  User,
  UserManager,
  WebStorageStateStore,
} from 'oidc-client-ts';
import {
  OidcValidationError,
  checkAuthorizationResponseIssuer,
  checkIdTokenClaims,
  safeReturnPath,
} from './oidc-checks';
import { ADMIN_SCOPES, APP_SETTINGS, AppSettings } from './settings';

/** The parts of oidc-client-ts's UserManager the admin UI uses; replaced in tests. */
export type OidcClient = Pick<
  UserManager,
  | 'signinRedirect'
  | 'signinRedirectCallback'
  | 'signoutRedirect'
  | 'signoutRedirectCallback'
  | 'removeUser'
>;

export const OIDC_CLIENT = new InjectionToken<OidcClient>('OIDC_CLIENT', {
  providedIn: 'root',
  factory: () => createOidcClient(inject(APP_SETTINGS)),
});

/** Replaces the browser URL without a navigation (to drop the code and state from the address bar). */
export const REPLACE_URL = new InjectionToken<(url: string) => void>('REPLACE_URL', {
  providedIn: 'root',
  factory: () => (url: string) => history.replaceState(null, '', url),
});

/** Seconds before expiry at which navigation renews the access token first. */
export const RENEW_BEFORE_SECONDS = 120;

export function createOidcClient(settings: AppSettings): UserManager {
  return new UserManager({
    authority: settings.issuer,
    client_id: settings.clientId,
    redirect_uri: `${settings.adminBase}callback`,
    post_logout_redirect_uri: settings.adminBase,
    response_type: 'code',
    scope: ADMIN_SCOPES,
    // Tokens live in memory only; a reload renews them through the SSO session (prompt=none).
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    // The PKCE verifier, state, and nonce must survive the redirect; they're removed by the callback.
    stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
    automaticSilentRenew: false,
    monitorSession: false,
    loadUserInfo: false,
    revokeTokensOnSignout: false,
    // Keep iss, aud, exp, ... in the profile so they can be validated (see checkIdTokenClaims).
    filterProtocolClaims: false,
  });
}

interface SigninState {
  readonly returnPath: string;
  readonly silent: boolean;
}

export type AuthStatus = 'pending' | 'signed-in' | 'signed-out' | 'error';

/**
 * Signs in through ValkyrieID itself with the seeded public PKCE client (PROJECT.md §8.2). The access token is kept
 * in memory only. There are no refresh tokens: a reload or an expiring token does a top-level redirect with
 * `prompt=none`, which the SSO session answers without UI (OIDC Core §3.1.2.1).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly settings = inject(APP_SETTINGS);
  private readonly client = inject(OIDC_CLIENT);
  private readonly replaceUrl = inject(REPLACE_URL);
  private readonly currentUser = signal<User | null>(null);

  readonly status = signal<AuthStatus>('pending');
  readonly error = signal<string | null>(null);
  /** The Admin API answered 401: the token expired or the session was revoked. */
  readonly sessionExpired = signal(false);
  /** The Admin API answered 403: the user isn't (or is no longer) an admin. */
  readonly forbidden = signal(false);
  /** The Admin API requires a sign-in with a second factor (RFC 9470); a silent renewal can't provide one. */
  readonly secondFactorRequired = signal(false);

  readonly accessToken = computed(() => this.currentUser()?.access_token ?? null);
  readonly displayName = computed(() => {
    const profile = this.currentUser()?.profile;
    return profile?.name ?? profile?.preferred_username ?? profile?.sub ?? '';
  });

  /** Seconds until the access token expires; 0 without one. */
  secondsLeft(now = Date.now()): number {
    const expiresAt = this.currentUser()?.expires_at;
    return expiresAt ? Math.max(0, expiresAt - Math.floor(now / 1000)) : 0;
  }

  /**
   * Runs before the router starts. Completes the sign-in or sign-out redirect, or starts a silent sign-in. While a
   * redirect is under way the returned promise never resolves, so nothing renders.
   */
  async initialize(href: string): Promise<void> {
    const url = new URL(href);
    const basePath = new URL(this.settings.adminBase).pathname;
    const route = url.pathname.startsWith(basePath) ? url.pathname.slice(basePath.length) : '';

    if (route === 'callback') {
      await this.completeSignIn(href);
      return;
    }

    // RP-Initiated Logout 1.0 §3: the post-logout redirect carries back our state.
    if (route === '' && url.searchParams.has('state')) {
      await this.completeSignOut(href, basePath);
      return;
    }

    return this.signIn(`/${route}${url.search}${url.hash}`, true);
  }

  /**
   * Redirects to the authorization endpoint; with `silent`, `prompt=none` reuses the SSO session. With `fresh`,
   * `prompt=login` asks for the credentials again, so the server can require a second factor.
   */
  signIn(returnPath: string, silent: boolean, fresh = false): Promise<void> {
    const state: SigninState = { returnPath: safeReturnPath(returnPath), silent };
    return this.redirect(() =>
      this.client.signinRedirect({
        state,
        nonce: randomToken(),
        prompt: silent ? 'none' : fresh ? 'login' : undefined,
      }),
    );
  }

  /** RP-initiated logout: ends the SSO session at ValkyrieID, then returns to the admin UI. */
  signOut(): Promise<void> {
    return this.redirect(() => this.client.signoutRedirect({ state: 'signed-out' }));
  }

  private async completeSignIn(href: string): Promise<void> {
    let user: User;
    try {
      checkAuthorizationResponseIssuer(href, this.settings.issuer);
      user = await this.client.signinRedirectCallback(href);
    } catch (error) {
      const state = error instanceof ErrorResponse ? toSigninState(error.state) : null;
      // OIDC Core §3.1.2.6: no SSO session (or it needs a fresh login); fall back to an interactive sign-in.
      if (
        state?.silent &&
        error instanceof ErrorResponse &&
        ['login_required', 'interaction_required', 'consent_required'].includes(error.error ?? '')
      ) {
        return this.signIn(state.returnPath, false);
      }

      return this.fail(error);
    }

    try {
      checkIdTokenClaims(
        user.profile,
        this.settings.issuer,
        this.settings.clientId,
        Math.floor(Date.now() / 1000),
      );
    } catch (error) {
      await this.client.removeUser();
      return this.fail(error);
    }

    const state = toSigninState(user.state);
    this.currentUser.set(user);
    this.sessionExpired.set(false);
    this.forbidden.set(false);
    this.secondFactorRequired.set(false);
    this.status.set('signed-in');
    this.replaceUrl(
      new URL(this.settings.adminBase).pathname + (state?.returnPath ?? '/').slice(1),
    );
  }

  private async completeSignOut(href: string, basePath: string): Promise<void> {
    try {
      await this.client.signoutRedirectCallback(href);
    } catch {
      // An unknown or replayed state: show the signed-out page anyway, there's no session here to keep.
    }

    this.currentUser.set(null);
    this.status.set('signed-out');
    this.replaceUrl(basePath);
  }

  private fail(error: unknown): void {
    this.currentUser.set(null);
    this.status.set('error');
    this.error.set(describe(error));
  }

  /** Never resolves once the browser is navigating away; resolves (with the error shown) if it couldn't start. */
  private async redirect(start: () => Promise<void>): Promise<void> {
    try {
      await start();
    } catch (error) {
      this.fail(error);
      return;
    }

    return new Promise<void>(() => undefined);
  }
}

function toSigninState(value: unknown): SigninState | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }

  const state = value as Partial<SigninState>;
  return { returnPath: safeReturnPath(state.returnPath), silent: state.silent === true };
}

function describe(error: unknown): string {
  if (error instanceof ErrorResponse) {
    return error.error_description ?? error.error ?? 'The sign-in failed.';
  }

  if (error instanceof OidcValidationError) {
    return error.message;
  }

  return 'The sign-in failed. Try again.';
}

function randomToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
}
