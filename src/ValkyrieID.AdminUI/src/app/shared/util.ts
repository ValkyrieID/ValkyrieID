import { Injectable, Resource, signal } from '@angular/core';

/**
 * The resource's value, or `undefined` while there is none. Unlike `value()`, it doesn't throw when the request
 * failed, so error states can still render (and show the error).
 */
export function valueOf<T>(resource: Resource<T>): T | undefined {
  return resource.hasValue() ? resource.value() : undefined;
}

/** Short status messages ("Saved.") announced through the shell's polite live region (WCAG 4.1.3). */
@Injectable({ providedIn: 'root' })
export class Notifier {
  readonly message = signal('');

  announce(message: string): void {
    // Clear first, so the same message twice is announced twice.
    this.message.set('');
    queueMicrotask(() => this.message.set(message));
  }
}

/** Splits a "one per line" textarea into trimmed, non-empty values. */
export function lines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** Trims a text field; empty means "not set". */
export function optional(text: string): string | null {
  const trimmed = text.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** An optional number input (`type="number"`); empty means "the default". */
export function optionalNumber(value: string | number | null): number | null {
  return value === null || value === '' ? null : Number(value);
}
