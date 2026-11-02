import { InjectionToken } from '@angular/core';

/** Where the admin UI and the Admin API live; everything is derived from the issuer URL the server renders. */
export interface AppSettings {
  /** The issuer (`iss`), exactly as published in discovery and tokens. */
  readonly issuer: string;
  /** The seeded public client (PROJECT.md §9). */
  readonly clientId: string;
  /** Absolute URL of the admin UI, ending in `/`. */
  readonly adminBase: string;
  /** Absolute URL of the Admin API, ending in `/`. */
  readonly apiBase: string;
}

export const ADMIN_CLIENT_ID = 'valkyrieid-admin-ui';
export const ADMIN_SCOPES = 'openid profile valkyrieid.admin';

export const APP_SETTINGS = new InjectionToken<AppSettings>('APP_SETTINGS');

/**
 * Reads the issuer from `<meta name="valkyrieid-issuer">`, which the server fills in from `Issuer:Url` (never from
 * the request). The UI and the API are only used under that origin: CSP `connect-src 'self'` and the API's lack of
 * CORS block any other.
 */
export function readSettings(doc: Document): AppSettings {
  const issuer =
    doc.querySelector<HTMLMetaElement>('meta[name="valkyrieid-issuer"]')?.content.trim() ?? '';
  if (!/^https?:\/\/[^/?#]+(\/[^?#]*)?$/.test(issuer) || issuer.endsWith('/')) {
    throw new Error('The issuer URL is missing from the page.');
  }

  return settingsFor(issuer);
}

export function settingsFor(issuer: string): AppSettings {
  return {
    issuer,
    clientId: ADMIN_CLIENT_ID,
    adminBase: `${issuer}/admin/`,
    apiBase: `${issuer}/api/admin/`,
  };
}
