import {
  OidcValidationError,
  checkAuthorizationResponseIssuer,
  checkIdTokenClaims,
  safeReturnPath,
} from './oidc-checks';

const issuer = 'https://id.example.test';
const clientId = 'valkyrieid-admin-ui';
const now = 1_800_000_000;

function claims(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    iss: issuer,
    aud: clientId,
    sub: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b',
    exp: now + 300,
    iat: now,
    nonce: 'n',
    ...overrides,
  };
}

describe('checkAuthorizationResponseIssuer (RFC 9207)', () => {
  it('accepts the expected issuer', () => {
    expect(() =>
      checkAuthorizationResponseIssuer(
        `${issuer}/admin/callback?code=c&state=s&iss=${encodeURIComponent(issuer)}`,
        issuer,
      ),
    ).not.toThrow();
  });

  it('accepts it on error responses too', () => {
    expect(() =>
      checkAuthorizationResponseIssuer(
        `${issuer}/admin/callback?error=login_required&state=s&iss=${encodeURIComponent(issuer)}`,
        issuer,
      ),
    ).not.toThrow();
  });

  it.each([
    ['another issuer (mix-up attack)', `iss=${encodeURIComponent('https://evil.example')}`],
    ['a missing iss', ''],
    ['a duplicated iss', `iss=${encodeURIComponent(issuer)}&iss=${encodeURIComponent(issuer)}`],
    ['a trailing slash', `iss=${encodeURIComponent(issuer + '/')}`],
  ])('rejects %s', (_case, query) => {
    expect(() =>
      checkAuthorizationResponseIssuer(`${issuer}/admin/callback?code=c&state=s&${query}`, issuer),
    ).toThrow(OidcValidationError);
  });
});

describe('checkIdTokenClaims (OIDC Core 3.1.3.7)', () => {
  it('accepts a valid ID token', () => {
    expect(() => checkIdTokenClaims(claims(), issuer, clientId, now)).not.toThrow();
  });

  it('accepts several audiences when azp is the client', () => {
    expect(() =>
      checkIdTokenClaims(
        claims({ aud: [clientId, 'other'], azp: clientId }),
        issuer,
        clientId,
        now,
      ),
    ).not.toThrow();
  });

  it.each([
    ['another issuer', { iss: 'https://evil.example' }],
    ['another audience', { aud: 'other-client' }],
    ['several audiences without azp', { aud: [clientId, 'other'] }],
    ['an azp of another client', { azp: 'other-client' }],
    ['an expired token', { exp: now - 61 }],
    ['a missing exp', { exp: undefined }],
    ['an iat in the future', { iat: now + 61 }],
    ['a missing subject', { sub: undefined }],
  ])('rejects %s', (_case, overrides) => {
    expect(() => checkIdTokenClaims(claims(overrides), issuer, clientId, now)).toThrow(
      OidcValidationError,
    );
  });

  it('allows the clock skew at the boundaries', () => {
    expect(() =>
      checkIdTokenClaims(claims({ exp: now - 59, iat: now + 60 }), issuer, clientId, now),
    ).not.toThrow();
    expect(() => checkIdTokenClaims(claims({ exp: now - 60 }), issuer, clientId, now)).toThrow(
      OidcValidationError,
    );
  });
});

describe('safeReturnPath', () => {
  it.each(['/', '/users', '/users/123?x=1#y', '/callbacks'])('keeps %s', (path) => {
    expect(safeReturnPath(path)).toBe(path);
  });

  it.each([
    ['https://evil.example/'],
    ['//evil.example/'],
    ['/\\evil.example'],
    ['users'],
    ['/callback'],
    ['/callback?code=x'],
    ['/a\nb'],
    [42],
    [null],
  ])('replaces %s with /', (value) => {
    expect(safeReturnPath(value)).toBe('/');
  });
});
