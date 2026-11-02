/** Checks oidc-client-ts doesn't do itself; see auth.service.ts. */

export class OidcValidationError extends Error {
  override readonly name = 'OidcValidationError';
}

/**
 * RFC 9207 §2.4: the client compares the `iss` parameter of the authorization response (success or error) with the
 * expected issuer and rejects the response if they differ. ValkyrieID always sends it
 * (`authorization_response_iss_parameter_supported`), so a missing parameter is rejected too.
 */
export function checkAuthorizationResponseIssuer(url: string, issuer: string): void {
  const iss = new URL(url).searchParams.getAll('iss');
  if (iss.length !== 1 || iss[0] !== issuer) {
    throw new OidcValidationError('The sign-in response came from an unexpected issuer.');
  }
}

export type IdTokenClaims = Readonly<Record<string, unknown>>;

/**
 * OIDC Core §3.1.3.7: ID token validation for the authorization code flow. oidc-client-ts checks the nonce (item 11)
 * but not these. The signature check (item 6) may be skipped: the token was received directly from the token
 * endpoint over TLS.
 */
export function checkIdTokenClaims(
  claims: IdTokenClaims,
  issuer: string,
  clientId: string,
  nowSeconds: number,
  skewSeconds = 60,
): void {
  // Item 2: iss exactly matches the issuer.
  if (claims['iss'] !== issuer) {
    throw new OidcValidationError('The ID token was issued by an unexpected issuer.');
  }

  // Item 3: aud contains the client id; items 4 and 5: with several audiences, azp is the client id.
  const aud = claims['aud'];
  const audiences = typeof aud === 'string' ? [aud] : Array.isArray(aud) ? aud : [];
  if (!audiences.includes(clientId)) {
    throw new OidcValidationError('The ID token is not for this client.');
  }

  const azp = claims['azp'];
  if ((audiences.length > 1 || azp !== undefined) && azp !== clientId) {
    throw new OidcValidationError('The ID token is not for this client.');
  }

  // Item 9: the current time is before exp; item 10: iat is not in the future.
  const exp = claims['exp'];
  const iat = claims['iat'];
  if (typeof exp !== 'number' || nowSeconds >= exp + skewSeconds) {
    throw new OidcValidationError('The ID token has expired.');
  }

  if (typeof iat !== 'number' || iat > nowSeconds + skewSeconds) {
    throw new OidcValidationError('The ID token was issued in the future.');
  }

  if (typeof claims['sub'] !== 'string' || claims['sub'].length === 0) {
    throw new OidcValidationError('The ID token has no subject.');
  }
}

/**
 * The route to return to after a sign-in redirect. Only app-relative paths are allowed, so a manipulated state can
 * never navigate elsewhere (RFC 9700 §4.11, open redirection).
 */
export function safeReturnPath(value: unknown): string {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) {
    return '/';
  }

  if (
    [...value].some((c) => c === '\\' || c.charCodeAt(0) < 0x20) ||
    /^\/callback(?:[/?#]|$)/.test(value)
  ) {
    return '/';
  }

  return value;
}
