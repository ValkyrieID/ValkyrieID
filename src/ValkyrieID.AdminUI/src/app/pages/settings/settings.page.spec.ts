import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SettingDto, SettingsSectionDto } from '../../api/models';
import { APP_SETTINGS, settingsFor } from '../../auth/settings';
import { SettingsPage } from './settings.page';
import { describeValue, splitDuration, toSeconds } from './settings-meta';

const api = 'https://id.example.test/api/admin';

function setting(overrides: Partial<SettingDto>): SettingDto {
  return {
    name: 'x',
    key: 'X:X',
    kind: 'string',
    value: null,
    defaultValue: null,
    locked: false,
    source: 'default',
    isSet: null,
    options: null,
    ...overrides,
  };
}

const lockout: SettingsSectionDto = {
  name: 'lockout',
  settings: [
    setting({
      name: 'maxFailedAttempts',
      key: 'Lockout:MaxFailedAttempts',
      kind: 'integer',
      value: 3,
      defaultValue: 5,
      locked: true,
      source: 'configuration',
    }),
    setting({
      name: 'duration',
      key: 'Lockout:Duration',
      kind: 'duration',
      value: 1800,
      defaultValue: 900,
      source: 'database',
    }),
  ],
};

const smtp: SettingsSectionDto = {
  name: 'smtp',
  settings: [
    setting({ name: 'host', key: 'Smtp:Host', value: 'smtp.example.test', source: 'database' }),
    setting({
      name: 'password',
      key: 'Smtp:Password',
      kind: 'secret',
      isSet: true,
      source: 'database',
    }),
  ],
};

describe('settings helpers', () => {
  it.each([
    [1800, 30, 'minutes'],
    [86400 * 14, 14, 'days'],
    [7200, 2, 'hours'],
    [45, 45, 'seconds'],
    [0, 0, 'seconds'],
  ])('splits %i seconds into %i %s', (seconds, amount, unit) => {
    expect(splitDuration(seconds)).toEqual({ amount, unit });
    expect(toSeconds(amount, unit as 'seconds')).toBe(seconds);
  });

  it('describes values', () => {
    expect(describeValue('duration', 900)).toBe('15 minutes');
    expect(describeValue('duration', 3600)).toBe('1 hour');
    expect(describeValue('boolean', true)).toBe('on');
    expect(describeValue('string', null)).toBe('none');
  });
});

describe('SettingsPage', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: APP_SETTINGS, useValue: settingsFor('https://id.example.test') },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function open(section: SettingsSectionDto) {
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.componentRef.setInput('section', section.name);
    TestBed.tick();
    backend.expectOne(`${api}/settings/${section.name}`).flush(section);
    await fixture.whenStable();
    return fixture;
  }

  it('shows settings from the configuration as locked, with a label for every control', async () => {
    const fixture = await open(lockout);
    const element = fixture.nativeElement as HTMLElement;

    const attempts = element.querySelector('#setting-maxFailedAttempts') as HTMLInputElement;
    expect(attempts.disabled).toBe(true);
    expect(attempts.value).toBe('3');
    const hint = element.querySelector(`#${attempts.getAttribute('aria-describedby')}`);
    expect(hint?.textContent).toContain('Set by configuration');
    expect(hint?.textContent).toContain('Lockout:MaxFailedAttempts');

    for (const control of Array.from(element.querySelectorAll('input, select'))) {
      expect(element.querySelector(`label[for="${control.id}"]`)).not.toBeNull();
    }

    const amount = element.querySelector('#setting-duration') as HTMLInputElement;
    const unit = element.querySelector('#setting-duration-unit') as HTMLSelectElement;
    expect(amount.disabled).toBe(false);
    expect(amount.value).toBe('30');
    expect(unit.value).toBe('minutes');
  });

  it('sends only the changed settings, in seconds', async () => {
    const fixture = await open(lockout);
    const element = fixture.nativeElement as HTMLElement;
    const save = element.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    const amount = element.querySelector('#setting-duration') as HTMLInputElement;
    amount.value = '2';
    amount.dispatchEvent(new Event('change'));
    const unit = element.querySelector('#setting-duration-unit') as HTMLSelectElement;
    unit.value = 'hours';
    unit.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(save.disabled).toBe(false);
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));

    const request = backend.expectOne(`${api}/settings/lockout`);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ duration: 7200 });
    request.flush(lockout);
  });

  it('resets a stored setting to its default with null', async () => {
    const fixture = await open(lockout);
    const element = fixture.nativeElement as HTMLElement;

    const reset = Array.from(element.querySelectorAll('button.link')).find((b) =>
      b.textContent?.includes('Use the default'),
    ) as HTMLButtonElement;
    reset.click();
    await fixture.whenStable();
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));

    const request = backend.expectOne(`${api}/settings/lockout`);
    expect(request.request.body).toEqual({ duration: null });
    request.flush(lockout);
  });

  it('never shows the smtp password and keeps it unless one is entered', async () => {
    const fixture = await open(smtp);
    const element = fixture.nativeElement as HTMLElement;
    const password = element.querySelector('#setting-password') as HTMLInputElement;
    expect(password.type).toBe('password');
    expect(password.value).toBe('');
    expect(element.querySelector('label[for="setting-password"]')?.textContent).toContain('(set)');

    const host = element.querySelector('#setting-host') as HTMLInputElement;
    host.value = 'mail.example.test';
    host.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    (element.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));

    const request = backend.expectOne(`${api}/settings/smtp`);
    expect(request.request.body).toEqual({ host: 'mail.example.test' });
    request.flush(smtp);
  });

  it('sends a test email', async () => {
    const fixture = await open(smtp);
    const element = fixture.nativeElement as HTMLElement;

    const button = Array.from(element.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Send test email'),
    ) as HTMLButtonElement;
    button.click();
    backend
      .expectOne(`${api}/settings/smtp/test`)
      .flush({ success: false, result: 'authentication_failed', to: 'admin@example.test' });
    await fixture.whenStable();

    expect(element.textContent).toContain('Sending failed: authentication_failed');
  });
});
