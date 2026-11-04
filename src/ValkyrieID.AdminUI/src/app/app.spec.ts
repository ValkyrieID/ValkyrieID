import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { AuthService, AuthStatus } from './auth/auth.service';

describe('App shell', () => {
  function render(status: AuthStatus, forbidden = false) {
    const auth = {
      status: signal(status),
      error: signal<string | null>('The user denied access.'),
      sessionExpired: signal(false),
      forbidden: signal(forbidden),
      secondFactorRequired: signal(false),
      displayName: signal('Alice'),
      signIn: vi.fn(() => Promise.resolve()),
      signOut: vi.fn(() => Promise.resolve()),
    };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, auth, root: fixture.nativeElement as HTMLElement };
  }

  it('has a skip link, a labelled navigation, and a main landmark', () => {
    const { root } = render('signed-in');

    expect(root.querySelector('a.skip-link')?.getAttribute('href')).toBe('#main');
    expect(root.querySelector('nav[aria-label="Main"]')).not.toBeNull();
    expect(root.querySelector('main#main')).not.toBeNull();
    expect(root.textContent).toContain('Alice');
  });

  it('signs out', () => {
    const { root, auth } = render('signed-in');

    (root.querySelector('.account button') as HTMLButtonElement).click();

    expect(auth.signOut).toHaveBeenCalled();
  });

  it('explains when the user is not an admin', () => {
    const { root } = render('signed-in', true);

    expect(root.querySelector('h1')?.textContent).toBe('Not authorized');
    expect(root.querySelector('router-outlet')).toBeNull();
  });

  it('shows the sign-in error and retries interactively', () => {
    const { root, auth } = render('error');

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('The user denied access.');
    (root.querySelector('main button') as HTMLButtonElement).click();
    expect(auth.signIn).toHaveBeenCalledWith('/', false);
  });

  it('offers to sign in again after signing out', () => {
    const { root } = render('signed-out');

    expect(root.textContent).toContain('You have signed out.');
  });
});
