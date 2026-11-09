import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { APP_SETTINGS, settingsFor } from '../../auth/settings';
import { UserListPage } from './user-list.page';

const api = 'https://id.example.test/api/admin';

describe('UserListPage', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: APP_SETTINGS, useValue: settingsFor('https://id.example.test') },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('lists users with their status', async () => {
    const fixture = TestBed.createComponent(UserListPage);
    TestBed.tick();

    backend.expectOne(`${api}/users?limit=50&offset=0`).flush({
      items: [
        {
          id: 'u1',
          userName: 'alice',
          email: 'alice@example.com',
          emailVerified: false,
          displayName: 'Alice',
          enabled: false,
          lockedOut: true,
          twoFactorEnabled: true,
          twoFactorRequired: false,
          roles: ['admin'],
          createdAt: '2026-11-01T10:00:00+00:00',
        },
      ],
      total: 1,
    });
    await fixture.whenStable();

    const row = fixture.nativeElement.querySelector('tbody tr') as HTMLElement;
    expect(row.querySelector('a')?.getAttribute('href')).toBe('/users/u1');
    expect(row.textContent).toContain('Email unverified');
    expect(row.textContent).toContain('Disabled');
    expect(row.textContent).toContain('Locked out');
    expect(row.textContent).toContain('admin');
  });

  it('searches from the first page', async () => {
    const fixture = TestBed.createComponent(UserListPage);
    TestBed.tick();
    backend.expectOne(`${api}/users?limit=50&offset=0`).flush({ items: [], total: 0 });
    await fixture.whenStable();

    const input = fixture.nativeElement.querySelector('input[type="search"]') as HTMLInputElement;
    input.value = 'bob & co';
    input.dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(
      new Event('submit'),
    );
    TestBed.tick();

    const request = backend.expectOne((r) => r.url.startsWith(`${api}/users?`));
    expect(request.request.urlWithParams).toContain('q=bob%20%26%20co');
    request.flush({ items: [], total: 0 });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('No users match your search.');
  });

  it('has a label for the search field', async () => {
    const fixture = TestBed.createComponent(UserListPage);
    TestBed.tick();
    backend.expectOne(`${api}/users?limit=50&offset=0`).flush({ items: [], total: 0 });

    const input = fixture.nativeElement.querySelector('input[type="search"]') as HTMLInputElement;
    const label = fixture.nativeElement.querySelector(`label[for="${input.id}"]`);
    expect(label?.textContent).toContain('Search');
  });
});
