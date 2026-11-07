import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { AuthService, OIDC_CLIENT, OidcClient, REPLACE_URL } from './auth.service';
import { freshTokenGuard } from './fresh-token.guard';
import { APP_SETTINGS, settingsFor } from './settings';

const issuer = 'https://id.example.test';

describe('freshTokenGuard', () => {
  let client: { [K in keyof OidcClient]: ReturnType<typeof vi.fn> };

  function setup(): AuthService {
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
        { provide: REPLACE_URL, useValue: () => undefined },
      ],
    });
    return TestBed.inject(AuthService);
  }

  function run(): unknown {
    return TestBed.runInInjectionContext(() =>
      freshTokenGuard({} as ActivatedRouteSnapshot, { url: '/users' } as RouterStateSnapshot),
    );
  }

  // Regression: the guard renewed the token on the sign-in error page, which replaced the error with a new sign-in
  // before it could be read.
  it('leaves the sign-in error page alone', async () => {
    const auth = setup();
    await auth.initialize(
      `${issuer}/admin/callback?code=c&state=s&iss=${encodeURIComponent('https://evil.example')}`,
    );
    expect(auth.status()).toBe('error');

    await expect(run()).resolves.toBe(true);
    expect(client.signinRedirect).not.toHaveBeenCalled();
  });

  it('leaves the signed-out page alone', async () => {
    const auth = setup();
    await auth.initialize(`${issuer}/admin/?state=signed-out`);
    expect(auth.status()).toBe('signed-out');

    await expect(run()).resolves.toBe(true);
    expect(client.signinRedirect).not.toHaveBeenCalled();
  });
});
