import { writeFileSync } from 'node:fs';
import { checkPage, expect, test } from '../support/a11y.ts';
import { adminStorageState, cli, stateFile, type RunState } from '../support/env.ts';
import { freshTotp } from '../support/totp.ts';
import { admin, alicePassword } from '../support/users.ts';

test('setup wizard and 2FA enrollment', async ({ page }) => {
  await page.goto('/setup');
  await expect(page).toHaveTitle(/^Set up – /);
  await checkPage(page, 'setup');

  // Validation errors: mismatched passwords.
  await page.getByLabel('Username').fill(admin.userName);
  await page.getByLabel('Email').fill(admin.email);
  await page.getByLabel('Password', { exact: true }).fill(admin.password);
  await page.getByLabel('Confirm password').fill('something else');
  await page.getByRole('button', { name: 'Create administrator' }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await checkPage(page, 'setup with errors');

  await page.getByLabel('Password', { exact: true }).fill(admin.password);
  await page.getByLabel('Confirm password').fill(admin.password);
  await page.getByRole('button', { name: 'Create administrator' }).click();

  // Admins must use 2FA: the session starts only after enrollment.
  await expect(
    page.getByRole('heading', { name: 'Set up two-factor authentication' }),
  ).toBeVisible();
  await checkPage(page, '2FA enrollment');
  const secret = (await page.locator('#totp-secret').textContent())?.trim() ?? '';
  expect(secret).not.toBe('');

  await page.getByLabel('Code from your authenticator app').fill('000000');
  await page.getByRole('button', { name: 'Turn on two-factor authentication' }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await checkPage(page, '2FA enrollment with errors');

  await page.getByLabel('Code from your authenticator app').fill(await freshTotp(secret));
  await page.getByRole('button', { name: 'Turn on two-factor authentication' }).click();
  await expect(
    page.getByRole('heading', { name: 'Two-factor authentication is on' }),
  ).toBeVisible();
  await checkPage(page, 'recovery codes');
  const recoveryCodes = await page.locator('.recovery-codes code').allTextContents();
  expect(recoveryCodes.length).toBeGreaterThan(1);

  await page.getByRole('link', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { name: 'Your account' })).toBeVisible();

  const state: RunState = { adminTotpSecret: secret, adminRecoveryCodes: recoveryCodes };
  writeFileSync(stateFile, JSON.stringify(state));
  await page.context().storageState({ path: adminStorageState });

  // Users and clients for the other tests, created like an operator would, with the CLI.
  await cli(
    ['user', 'create', 'alice', '--email', 'alice@example.test', '--name', 'Alice'],
    alicePassword,
  );
  await cli([
    'client',
    'create',
    'webapp',
    '--public',
    '--grant',
    'authorization_code',
    '--redirect-uri',
    'http://127.0.0.1:8765/callback',
    '--post-logout-redirect-uri',
    'http://127.0.0.1:8765/signed-out',
    '--scope',
    'openid',
    '--scope',
    'profile',
    '--name',
    'Web app',
  ]);
  await cli([
    'resource',
    'create',
    'orders',
    '--scope',
    'orders.read',
    '--scope',
    'orders.write',
    '--name',
    'Orders API',
  ]);
});
