import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';

/** Shared with the login pages, which render it on the server (PROJECT.md §8.1). */
export const THEME_COOKIE = '__Host-valkyrie.theme';

const MAX_AGE_SECONDS = 365 * 24 * 60 * 60;

const THEME_COLORS = { light: '#ffffff', dark: '#0a0a0b' } as const;

/**
 * The light/dark/system theme choice. index.html starts with data-theme="dark"; initialize() replaces it with the
 * stored choice before the first render, so the page can go from dark to light but never flashes light first.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly state = signal<ThemeMode>('system');

  readonly mode = this.state.asReadonly();

  initialize(): void {
    this.state.set(parse(readCookie(this.document.cookie, THEME_COOKIE)));
    this.apply();
  }

  set(mode: ThemeMode): void {
    this.state.set(mode);
    // The __Host- prefix needs Secure and Path=/ without a Domain. System deletes the cookie.
    this.document.cookie =
      mode === 'system'
        ? `${THEME_COOKIE}=; Path=/; Max-Age=0; Secure; SameSite=Lax`
        : `${THEME_COOKIE}=${mode}; Path=/; Max-Age=${MAX_AGE_SECONDS}; Secure; SameSite=Lax`;
    this.apply();
  }

  private apply(): void {
    const mode = this.state();
    // "system" too, like the login pages; the stylesheets then follow prefers-color-scheme.
    this.document.documentElement.setAttribute('data-theme', mode);

    const prefersDark =
      mode === 'dark' ||
      (mode === 'system' &&
        (this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false));
    this.document
      .querySelector('meta[name="theme-color"]')
      ?.setAttribute('content', prefersDark ? THEME_COLORS.dark : THEME_COLORS.light);
  }
}

/** Only the known values; anything else follows the system, like on the server. */
function parse(value: string | undefined): ThemeMode {
  return value === 'light' || value === 'dark' ? value : 'system';
}

function readCookie(cookies: string, name: string): string | undefined {
  for (const part of cookies.split(';')) {
    const separator = part.indexOf('=');
    if (separator > 0 && part.slice(0, separator).trim() === name) {
      return part.slice(separator + 1).trim();
    }
  }

  return undefined;
}
