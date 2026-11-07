import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import {
  SettingDto,
  SettingValue,
  SettingsDto,
  SettingsSectionDto,
  SmtpTestResult,
} from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, valueOf } from '../../shared/util';
import {
  DURATION_UNITS,
  DurationUnit,
  SECTIONS,
  describeValue,
  fieldMeta,
  sectionMeta,
  splitDuration,
  toSeconds,
} from './settings-meta';

/**
 * Runtime settings (docs/reference/configuration.md). `/settings` lists the sections; `/settings/:section` edits
 * one. Settings set in the server's configuration are shown read-only: configuration takes precedence.
 */
@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ProblemComponent],
  templateUrl: './settings.page.html',
})
export class SettingsPage {
  private readonly api = inject(AdminApi);
  private readonly notifier = inject(Notifier);

  /** The route's `:section`; undefined on the overview. */
  readonly section = input<string>();

  protected readonly sections = Object.entries(SECTIONS).map(([name, meta]) => ({ name, ...meta }));
  protected readonly units = DURATION_UNITS;
  protected readonly meta = computed(() => sectionMeta(this.section() ?? ''));

  protected readonly overview = httpResource<SettingsDto>(() =>
    this.section() ? undefined : this.api.url(['settings']),
  );
  protected readonly data = httpResource<SettingsSectionDto>(() => {
    const section = this.section();
    return section ? this.api.url(['settings', section]) : undefined;
  });
  protected readonly loadProblem = computed(() => {
    const error = this.data.error() ?? this.overview.error();
    return error ? toProblem(error) : null;
  });

  /** Edited values by setting name; reset whenever the section is (re)loaded or saved. */
  protected readonly draft = linkedSignal<
    SettingsSectionDto | undefined,
    Record<string, SettingValue>
  >({
    source: () => valueOf(this.data),
    computation: () => ({}),
  });
  /** The unit each duration is shown in. */
  protected readonly durationUnits = linkedSignal<
    SettingsSectionDto | undefined,
    Record<string, DurationUnit>
  >({
    source: () => valueOf(this.data),
    computation: (section) =>
      Object.fromEntries(
        (section?.settings ?? [])
          .filter((s) => s.kind === 'duration')
          .map((s) => [s.name, splitDuration(Number(s.value ?? 0)).unit]),
      ),
  });
  protected readonly dirty = computed(() => Object.keys(this.draft()).length > 0);
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly testResult = signal<SmtpTestResult | null>(null);

  /** Settings from the server's configuration, per section, for the overview. */
  protected lockedCount(name: string): number {
    return (
      this.overview
        .value()
        ?.sections.find((s) => s.name === name)
        ?.settings.filter((s) => s.locked).length ?? 0
    );
  }

  protected label(setting: SettingDto): string {
    return fieldMeta(this.section() ?? '', setting.name).label;
  }

  protected help(setting: SettingDto): string | undefined {
    return fieldMeta(this.section() ?? '', setting.name).help;
  }

  protected id(setting: SettingDto): string {
    return `setting-${setting.name}`;
  }

  protected describe(setting: SettingDto, value: SettingValue): string {
    return describeValue(setting.kind, value);
  }

  protected edited(setting: SettingDto): boolean {
    return setting.name in this.draft();
  }

  /** The value shown in the form: the edit if there is one, otherwise the current value. */
  protected current(setting: SettingDto): SettingValue {
    const draft = this.draft();
    return setting.name in draft ? draft[setting.name] : setting.value;
  }

  /** The effective value after a reset: the default. */
  protected shown(setting: SettingDto): SettingValue {
    const value = this.current(setting);
    return value === null && this.edited(setting) && setting.kind !== 'secret'
      ? setting.defaultValue
      : value;
  }

  protected amount(setting: SettingDto): string {
    const seconds = this.shown(setting);
    if (typeof seconds !== 'number') {
      return '';
    }

    const unit = DURATION_UNITS.find((u) => u.unit === this.durationUnits()[setting.name]);
    return String(seconds / (unit?.seconds ?? 1));
  }

  protected set(setting: SettingDto, value: SettingValue): void {
    this.draft.update((draft) => ({ ...draft, [setting.name]: value }));
  }

  protected unset(setting: SettingDto): void {
    this.draft.update((draft) =>
      Object.fromEntries(Object.entries(draft).filter(([name]) => name !== setting.name)),
    );
  }

  protected setText(setting: SettingDto, event: Event): void {
    const text = (event.target as HTMLInputElement | HTMLSelectElement).value;
    if (setting.kind === 'secret') {
      // Empty keeps the stored password.
      if (text === '') {
        this.unset(setting);
      } else {
        this.set(setting, text);
      }

      return;
    }

    this.set(setting, text.trim() === '' ? null : text.trim());
  }

  protected setInteger(setting: SettingDto, event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.set(setting, text === '' ? null : Number(text));
  }

  protected setBoolean(setting: SettingDto, event: Event): void {
    this.set(setting, (event.target as HTMLInputElement).checked);
  }

  protected setAmount(setting: SettingDto, event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    const unit = this.durationUnits()[setting.name] ?? 'seconds';
    this.set(setting, text === '' ? null : Math.round(toSeconds(Number(text), unit)));
  }

  /** Changing the unit keeps the number, e.g. 30 minutes becomes 30 hours. */
  protected setUnit(setting: SettingDto, event: Event): void {
    const unit = (event.target as HTMLSelectElement).value as DurationUnit;
    const amount = this.amount(setting);
    this.durationUnits.update((units) => ({ ...units, [setting.name]: unit }));
    if (amount !== '') {
      this.set(setting, Math.round(toSeconds(Number(amount), unit)));
    }
  }

  protected removeSecret(setting: SettingDto, event: Event): void {
    if ((event.target as HTMLInputElement).checked) {
      this.set(setting, null);
    } else {
      this.unset(setting);
    }
  }

  protected discard(): void {
    this.draft.set({});
    this.problem.set(null);
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    const section = this.section();
    if (!section || !this.dirty()) {
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    try {
      this.data.value.set(await this.api.updateSettings(section, this.draft()));
      this.notifier.announce('Settings saved.');
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async sendTestEmail(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    this.testResult.set(null);
    try {
      const result = await this.api.sendTestEmail();
      this.testResult.set(result);
      this.notifier.announce(
        result.success ? `Test email sent to ${result.to}.` : `Sending failed: ${result.result}.`,
      );
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
