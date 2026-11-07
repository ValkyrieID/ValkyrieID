import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { APP_SETTINGS, settingsFor } from '../../auth/settings';
import { UserDetailPage } from './user-detail.page';

const api = 'https://id.example.test/api/admin';

describe('UserDetailPage', () => {
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

  // Regression: reading value() of a failed resource throws, which left the page blank (no heading, no error).
  it('shows a heading and the error when the user is not found', async () => {
    const fixture = TestBed.createComponent(UserDetailPage);
    fixture.componentRef.setInput('id', 'missing');
    TestBed.tick();

    const notFound = {
      status: 404,
      statusText: 'Not Found',
      headers: { 'Content-Type': 'application/problem+json' },
    };
    backend
      .expectOne(`${api}/users/missing`)
      .flush({ title: 'User not found.', status: 404 }, notFound);
    backend
      .expectOne(`${api}/users/missing/sessions`)
      .flush({ title: 'User not found.', status: 404 }, notFound);
    for (const request of backend.match(() => true)) {
      request.flush({ items: [], total: 0 });
    }
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent).toBe('User');
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('User not found.');
    expect(element.textContent).not.toContain('Loading');
  });
});
