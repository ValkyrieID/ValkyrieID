import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ThemeMode, ThemeService } from './theme.service';

/** Toggle buttons for the theme; the pressed one is the current choice (same pattern as the login pages). */
@Component({
  selector: 'app-theme-switch',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './theme-switch.component.html',
})
export class ThemeSwitchComponent {
  protected readonly theme = inject(ThemeService);

  protected readonly options: readonly { value: ThemeMode; label: string }[] = [
    { value: 'system', label: 'System theme' },
    { value: 'light', label: 'Light theme' },
    { value: 'dark', label: 'Dark theme' },
  ];
}
