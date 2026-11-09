import { TestBed } from '@angular/core/testing';
import { IdComponent } from './id.component';
import { Notifier } from './util';

const uuid = '1a2b3c4d-0000-4000-8000-000000000001';

describe('IdComponent', () => {
  let writeText: ReturnType<typeof vi.fn>;
  let original: PropertyDescriptor | undefined;

  beforeEach(() => {
    original = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
  });

  afterEach(() => {
    if (original) {
      Object.defineProperty(navigator, 'clipboard', original);
    } else {
      delete (navigator as { clipboard?: unknown }).clipboard;
    }
  });

  function render(inputs: Record<string, unknown>) {
    const fixture = TestBed.createComponent(IdComponent);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('shows the short form and keeps the full value as text', () => {
    const { element } = render({ value: uuid, label: 'user ID' });

    expect(element.querySelector('[aria-hidden="true"]')?.textContent).toBe('1a2b3c4d…');
    expect(element.querySelector('.visually-hidden')?.textContent).toBe(uuid);
  });

  it('shows short values once, without hidden text', () => {
    const { element } = render({ value: 'cli' });

    expect(element.querySelector('code')?.textContent).toBe('cli');
    expect(element.querySelector('.visually-hidden')).toBeNull();
  });

  it('shows the full value when asked', () => {
    const { element } = render({ value: uuid, full: true });

    expect(element.querySelector('code')?.textContent).toBe(uuid);
  });

  it('copies the full value and announces it', async () => {
    const notifier = TestBed.inject(Notifier);
    const { fixture, element } = render({ value: uuid, label: 'user ID' });

    const button = element.querySelector('button') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toBe('Copy user ID');
    button.click();
    await fixture.whenStable();
    await Promise.resolve();

    expect(writeText).toHaveBeenCalledWith(uuid);
    expect(notifier.message()).toBe('User ID copied.');
    fixture.detectChanges();
    expect(element.textContent).toContain('Copied');
  });

  it('announces a failed copy', async () => {
    writeText.mockImplementation(() => Promise.reject(new Error('denied')));
    const notifier = TestBed.inject(Notifier);
    const { fixture, element } = render({ value: uuid, label: 'user ID' });

    (element.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    await Promise.resolve();

    expect(notifier.message()).toBe("The user ID couldn't be copied.");
  });

  it('has no copy button without the Clipboard API, or when turned off', () => {
    expect(render({ value: uuid, allowCopy: false }).element.querySelector('button')).toBeNull();

    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    expect(render({ value: uuid }).element.querySelector('button')).toBeNull();
  });
});
