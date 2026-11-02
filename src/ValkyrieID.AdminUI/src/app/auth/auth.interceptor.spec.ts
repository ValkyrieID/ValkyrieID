import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';
import { APP_SETTINGS, settingsFor } from './settings';

const issuer = 'https://id.example.test';

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let auth: {
    accessToken: () => string | null;
    sessionExpired: { set: ReturnType<typeof vi.fn> };
    forbidden: { set: ReturnType<typeof vi.fn> };
  };

  beforeEach(() => {
    auth = {
      accessToken: () => 'secret-token',
      sessionExpired: { set: vi.fn() },
      forbidden: { set: vi.fn() },
    };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: APP_SETTINGS, useValue: settingsFor(issuer) },
        { provide: AuthService, useValue: auth },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('adds the token to Admin API requests', () => {
    http.get(`${issuer}/api/admin/users`).subscribe();

    expect(
      backend.expectOne(`${issuer}/api/admin/users`).request.headers.get('Authorization'),
    ).toBe('Bearer secret-token');
  });

  it.each([
    [`${issuer}/connect/userinfo`],
    [`${issuer}/api/administrator`],
    [`${issuer}/admin/manifest.webmanifest`],
    ['https://evil.example/api/admin/users'],
    [`${issuer}.evil.example/api/admin/users`],
    ['/api/admin/users'],
  ])('never sends it to %s', (url) => {
    http.get(url).subscribe();

    expect(backend.expectOne(url).request.headers.has('Authorization')).toBe(false);
  });

  it('flags an expired session on 401', async () => {
    const response = firstValueFrom(http.get(`${issuer}/api/admin/users`));
    backend
      .expectOne(`${issuer}/api/admin/users`)
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(response).rejects.toBeTruthy();
    expect(auth.sessionExpired.set).toHaveBeenCalledWith(true);
  });

  it('flags a missing admin role on 403', async () => {
    const response = firstValueFrom(http.get(`${issuer}/api/admin/users`));
    backend
      .expectOne(`${issuer}/api/admin/users`)
      .flush(null, { status: 403, statusText: 'Forbidden' });

    await expect(response).rejects.toBeTruthy();
    expect(auth.forbidden.set).toHaveBeenCalledWith(true);
  });
});
