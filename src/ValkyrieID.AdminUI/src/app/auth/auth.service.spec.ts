import { TestBed } from '@angular/core/testing';
import { ErrorResponse, User } from 'oidc-client-ts';
import { AuthService, OIDC_CLIENT, OidcClient, REPLACE_URL } from './auth.service';
import { APP_SETTINGS, settingsFor } from './settings';

const issuer = 'https://id.example.test';
const iss = encodeURIComponent(issuer);

function user(profile: Record<string, unknown> = {}, state: unknown = { returnPath: '/users' }) {
  const now = Math.floor(Date.now() / 1000);
  return new User({
    access_token: 'access-token',
    token_type: 'Bearer',
    id_token: 'id-token',
    scope: 'openid profile valkyrieid.admin',
    expires_at: now + 600,
    userState: state,
    profile: {
      iss: issuer,
      aud: 'valkyrieid-admin-ui',
      sub: '0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b',
      exp: now + 300,
      iat: now,
      name: 'Alice',
      ...profile,
    } as User['profile'],
  });
}

describe('AuthService', () => {
  let client: {
    [K in keyof OidcClient]: ReturnType<typeof vi.fn>;
  };
  let replaced: string[];

  function setup(): AuthService {
    replaced = [];
    client = {
      signinRedirect: vi.fn(() => Promise.resolve()),
      signinRedirectCallback: vi.fn(),
      signoutRedirect: vi.fn(() => Promise.resolve()),
      signoutRedirectCallback: vi.fn(() => Promise.resolve({})),
      removeUser: vi.fn(() => Promise.resolve()),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: APP_SETTINGS, useValue: settingsFor(issuer) },
        { provide: OIDC_CLIENT, useValue: client },
        { provide: REPLACE_URL, useValue: (url: string) => replaced.push(url) },
      ],
    });
    return TestBed.inject(AuthService);
  }

  it('starts a silent sign-in that returns to the current page', async () => {
    const auth = setup();

    void auth.initialize(`${issuer}/admin/users/42?tab=roles`);
    await Promise.resolve();

    expect(client.signinRedirect).toHaveBeenCalledOnce();
    const args = client.signinRedirect.mock.calls[0][0];
    expect(args.prompt).toBe('none');
    expect(args.state).toEqual({ returnPath: '/users/42?tab=roles', silent: true });
    expect(args.nonce).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(auth.status()).toBe('pending');
  });

  it('completes the sign-in and drops the code from the URL', async () => {
    const auth = setup();
    client.signinRedirectCallback.mockResolvedValue(user());

    await auth.initialize(`${issuer}/admin/callback?code=c&state=s&iss=${iss}`);

    expect(auth.status()).toBe('signed-in');
    expect(auth.accessToken()).toBe('access-token');
    expect(auth.displayName()).toBe('Alice');
    expect(replaced).toEqual(['/admin/users']);
  });

  it('rejects a response from another issuer before redeeming the code', async () => {
    const auth = setup();

    await auth.initialize(
      `${issuer}/admin/callback?code=c&state=s&iss=${encodeURIComponent('https://evil.example')}`,
    );

    expect(client.signinRedirectCallback).not.toHaveBeenCalled();
    expect(auth.status()).toBe('error');
    expect(auth.accessToken()).toBeNull();
  });

  it('rejects an ID token for another client', async () => {
    const auth = setup();
    client.signinRedirectCallback.mockResolvedValue(user({ aud: 'other-client' }));

    await auth.initialize(`${issuer}/admin/callback?code=c&state=s&iss=${iss}`);

    expect(auth.status()).toBe('error');
    expect(auth.accessToken()).toBeNull();
    expect(client.removeUser).toHaveBeenCalled();
  });

  it('never returns to an absolute URL from the state', async () => {
    const auth = setup();
    client.signinRedirectCallback.mockResolvedValue(
      user({}, { returnPath: '//evil.example/', silent: false }),
    );

    await auth.initialize(`${issuer}/admin/callback?code=c&state=s&iss=${iss}`);

    expect(replaced).toEqual(['/admin/']);
  });

  it('falls back to an interactive sign-in without an SSO session', async () => {
    const auth = setup();
    client.signinRedirectCallback.mockRejectedValue(
      new ErrorResponse({
        error: 'login_required',
        userState: { returnPath: '/clients', silent: true },
      }),
    );

    void auth.initialize(`${issuer}/admin/callback?error=login_required&state=s&iss=${iss}`);
    await vi.waitFor(() => expect(client.signinRedirect).toHaveBeenCalledOnce());

    const args = client.signinRedirect.mock.calls[0][0];
    expect(args.prompt).toBeUndefined();
    expect(args.state).toEqual({ returnPath: '/clients', silent: false });
  });

  it("doesn't loop when an interactive sign-in fails", async () => {
    const auth = setup();
    client.signinRedirectCallback.mockRejectedValue(
      new ErrorResponse({
        error: 'access_denied',
        error_description: 'The user denied access.',
        userState: { returnPath: '/', silent: false },
      }),
    );

    await auth.initialize(`${issuer}/admin/callback?error=access_denied&state=s&iss=${iss}`);

    expect(client.signinRedirect).not.toHaveBeenCalled();
    expect(auth.status()).toBe('error');
    expect(auth.error()).toBe('The user denied access.');
  });

  it('shows the signed-out page after logout', async () => {
    const auth = setup();

    await auth.initialize(`${issuer}/admin/?state=xyz`);

    expect(client.signoutRedirectCallback).toHaveBeenCalled();
    expect(client.signinRedirect).not.toHaveBeenCalled();
    expect(auth.status()).toBe('signed-out');
    expect(replaced).toEqual(['/admin/']);
  });

  it('signs out at the end-session endpoint', async () => {
    const auth = setup();

    void auth.signOut();
    await Promise.resolve();

    expect(client.signoutRedirect).toHaveBeenCalledWith({ state: 'signed-out' });
  });

  it('shows an error when the redirect cannot start', async () => {
    const auth = setup();
    client.signinRedirect.mockRejectedValue(new Error('network'));

    await auth.initialize(`${issuer}/admin/`);

    expect(auth.status()).toBe('error');
  });
});
