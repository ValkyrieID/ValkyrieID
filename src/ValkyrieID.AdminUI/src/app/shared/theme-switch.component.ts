import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ThemeMode, ThemeService } from './theme.service';

/** Toggle buttons for the theme; the pressed one is the current choice (same pattern as the login pages). */
@Component({
  selector: 'app-theme-switch',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="theme-switch" role="group" aria-label="Color theme">
      @for (option of options; track option.value) {
        <button
          type="button"
          [attr.aria-pressed]="theme.mode() === option.value"
          [attr.aria-label]="option.label"
          [title]="option.label"
          (click)="theme.set(option.value)"
        >
          <svg
            viewBox="0 0 24 24"
            width="16"
            height="16"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
            focusable="false"
          >
            @switch (option.value) {
              @case ('system') {
                <svg:rect x="2" y="3" width="20" height="14" rx="2" />
                <svg:path d="M8 21h8M12 17v4" />
              }
              @case ('light') {
                <svg:circle cx="12" cy="12" r="4" />
                <svg:path
                  d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"
                />
              }
              @case ('dark') {
                <svg:path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
              }
            }
          </svg>
        </button>
      }
    </div>
  `,
})
export class ThemeSwitchComponent {
  protected readonly theme = inject(ThemeService);

  protected readonly options: readonly { value: ThemeMode; label: string }[] = [
    { value: 'system', label: 'System theme' },
    { value: 'light', label: 'Light theme' },
    { value: 'dark', label: 'Dark theme' },
  ];
}
