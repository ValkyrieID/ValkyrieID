import { TestBed } from '@angular/core/testing';
import { THEME_COOKIE, ThemeService } from './theme.service';

describe('ThemeService', () => {
  let cookie: string;
  let written: string[];
  let meta: HTMLMetaElement;

  beforeEach(() => {
    cookie = '';
    written = [];
    // jsdom drops Secure cookies on http://, so record what the service writes instead.
    Object.defineProperty(document, 'cookie', {
      configurable: true,
      get: () => cookie,
      set: (value: string) => written.push(value),
    });
    // As served: index.html starts dark.
    document.documentElement.setAttribute('data-theme', 'dark');
    meta = document.createElement('meta');
    meta.name = 'theme-color';
    meta.content = '#0a0a0b';
    document.head.append(meta);
  });

  afterEach(() => {
    delete (document as { cookie?: string }).cookie;
    document.documentElement.removeAttribute('data-theme');
    meta.remove();
  });

  function create(): ThemeService {
    return TestBed.inject(ThemeService);
  }

  it('follows the system when there is no stored choice, replacing the initial dark theme', () => {
    const theme = create();

    theme.initialize();

    expect(theme.mode()).toBe('system');
    expect(document.documentElement.getAttribute('data-theme')).toBe('system');
  });

  it('applies a stored light choice', () => {
    cookie = `other=1; ${THEME_COOKIE}=light`;
    const theme = create();

    theme.initialize();

    expect(theme.mode()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(meta.content).toBe('#ffffff');
  });

  it('keeps a stored dark choice', () => {
    cookie = `${THEME_COOKIE}=dark`;
    const theme = create();

    theme.initialize();

    expect(theme.mode()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(meta.content).toBe('#0a0a0b');
  });

  it('ignores unknown stored values', () => {
    cookie = `${THEME_COOKIE}=%22%3E%3Cscript%3E`;
    const theme = create();

    theme.initialize();

    expect(theme.mode()).toBe('system');
    expect(document.documentElement.getAttribute('data-theme')).toBe('system');
  });

  it('stores an explicit choice in a host-only, secure cookie', () => {
    const theme = create();
    theme.initialize();

    theme.set('light');

    expect(theme.mode()).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(written).toEqual([
      `${THEME_COOKIE}=light; Path=/; Max-Age=31536000; Secure; SameSite=Lax`,
    ]);
  });

  it('deletes the cookie when switching back to system', () => {
    cookie = `${THEME_COOKIE}=dark`;
    const theme = create();
    theme.initialize();

    theme.set('system');

    expect(document.documentElement.getAttribute('data-theme')).toBe('system');
    expect(written).toEqual([`${THEME_COOKIE}=; Path=/; Max-Age=0; Secure; SameSite=Lax`]);
  });
});
