import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ThemeSwitchComponent } from './theme-switch.component';
import { ThemeMode, ThemeService } from './theme.service';

describe('ThemeSwitchComponent', () => {
  function render(mode: ThemeMode) {
    const state = signal(mode);
    const theme = { mode: state.asReadonly(), set: vi.fn((m: ThemeMode) => state.set(m)) };
    TestBed.configureTestingModule({ providers: [{ provide: ThemeService, useValue: theme }] });
    const fixture = TestBed.createComponent(ThemeSwitchComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const buttons = Array.from(root.querySelectorAll('button'));
    return { fixture, theme, root, buttons };
  }

  it('is a labelled group of toggle buttons with the current choice pressed', () => {
    const { root, buttons } = render('light');

    expect(root.querySelector('[role="group"]')?.getAttribute('aria-label')).toBe('Color theme');
    expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual([
      'System theme',
      'Light theme',
      'Dark theme',
    ]);
    expect(buttons.map((b) => b.title)).toEqual(['System theme', 'Light theme', 'Dark theme']);
    expect(buttons.every((b) => b.querySelector('svg[aria-hidden="true"]'))).toBe(true);
    expect(buttons.map((b) => b.getAttribute('aria-pressed'))).toEqual(['false', 'true', 'false']);
    expect(buttons.every((b) => b.type === 'button')).toBe(true);
  });

  it('changes the theme and updates the pressed state', async () => {
    const { fixture, theme, buttons } = render('system');

    buttons[2].click();
    await fixture.whenStable();

    expect(theme.set).toHaveBeenCalledWith('dark');
    expect(buttons.map((b) => b.getAttribute('aria-pressed'))).toEqual(['false', 'false', 'true']);
  });
});
