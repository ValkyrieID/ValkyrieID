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
          (click)="theme.set(option.value)"
        >
          {{ option.label }}
        </button>
      }
    </div>
  `,
})
export class ThemeSwitchComponent {
  protected readonly theme = inject(ThemeService);

  protected readonly options: readonly { value: ThemeMode; label: string }[] = [
    { value: 'system', label: 'System' },
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
  ];
}
