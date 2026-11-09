import { canCopy, shortId } from './ids';

describe('shortId', () => {
  it('shortens a UUID to its first 8 characters', () => {
    expect(shortId('1a2b3c4d-0000-4000-8000-000000000001')).toBe('1a2b3c4d…');
  });

  it('shortens UUIDs inside a value and keeps the rest', () => {
    expect(shortId('admin:1A2B3C4D-0000-4000-8000-000000000001')).toBe('admin:1A2B3C4D…');
  });

  it('keeps short values unchanged', () => {
    expect(shortId('cli')).toBe('cli');
    expect(shortId('system')).toBe('system');
  });

  it('cuts other long values', () => {
    expect(shortId('abcdefghijklmnopqrstuvwxyz0123')).toBe('abcdefghijkl…');
  });
});

describe('canCopy', () => {
  it('reflects the Clipboard API', () => {
    const original = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    try {
      Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
      expect(canCopy()).toBe(false);
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: () => Promise.resolve() },
        configurable: true,
      });
      expect(canCopy()).toBe(true);
    } finally {
      if (original) {
        Object.defineProperty(navigator, 'clipboard', original);
      } else {
        delete (navigator as { clipboard?: unknown }).clipboard;
      }
    }
  });
});
