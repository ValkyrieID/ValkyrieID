// Shared by the measurement scripts (scripts/measure-contrast.ts, scripts/lighthouse.ts): signs in as the suite's
// administrator on a running test server (node scripts/serve.ts) and reaches the admin pages by in-app navigation.
import { existsSync, writeFileSync } from 'node:fs';
import type { Page } from '@playwright/test';
import { cli, readState, stateFile, type RunState } from './env.ts';
import { freshTotp } from './totp.ts';
import { admin, alicePassword } from './users.ts';

/** Signs in as the administrator, setting up a fresh server like tests/setup.spec.ts. Ends on /account. */
export async function signIn(page: Page): Promise<void> {
  await page.goto('/setup');
  if (await page.getByRole('button', { name: 'Create administrator' }).isVisible()) {
    await page.getByLabel('Username').fill(admin.userName);
    await page.getByLabel('Email').fill(admin.email);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByLabel('Confirm password').fill(admin.password);
    await page.getByRole('button', { name: 'Create administrator' }).click();
    const secret = ((await page.locator('#totp-secret').textContent()) ?? '').trim();
    await page.getByLabel('Code from your authenticator app').fill(await freshTotp(secret));
    await page.getByRole('button', { name: 'Turn on two-factor authentication' }).click();
    await page.getByRole('heading', { name: 'Two-factor authentication is on' }).waitFor();
    const recoveryCodes = await page.locator('.recovery-codes code').allTextContents();
    const state: RunState = { adminTotpSecret: secret, adminRecoveryCodes: recoveryCodes };
    writeFileSync(stateFile, JSON.stringify(state));
    await page.getByRole('link', { name: 'Continue' }).click();

    // The same data as the accessibility suite.
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
  } else {
    if (!existsSync(stateFile)) {
      throw new Error(
        `${stateFile} not found: start a fresh test server (node scripts/serve.ts) first.`,
      );
    }

    await page.goto('/account/login?returnUrl=%2Faccount');
    const yourAccount = page.getByRole('heading', { name: 'Your account' });
    const userName = page.getByLabel('Username or email');
    await yourAccount.or(userName).waitFor();
    if (await yourAccount.isVisible()) {
      return;
    }

    await userName.fill(admin.userName);
    await page.getByLabel('Password').fill(admin.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page
      .getByLabel('Code from your authenticator app')
      .fill(await freshTotp(readState().adminTotpSecret));
    await page.getByRole('button', { name: 'Verify' }).click();
  }

  await page.getByRole('heading', { name: 'Your account' }).waitFor();
}

export interface PageState {
  name: string;
  /** Gets there from the previous state. */
  open: (page: Page) => Promise<void>;
}

/** Waits for the page's <h1>, the network, and any loading indicator. */
export const heading = async (page: Page, name: string | RegExp): Promise<void> => {
  await page.getByRole('heading', { level: 1, name }).waitFor();
  await page.waitForLoadState('networkidle');
  await page
    .locator('app-loading')
    .first()
    .waitFor({ state: 'detached' })
    .catch(() => undefined);
};

/** Follows a link in the admin navigation. */
export const nav =
  (link: string, title: string | RegExp): PageState['open'] =>
  async (page) => {
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: link, exact: true })
      .click();
    await heading(page, title);
  };

/** Follows a link in the page content. */
export const row =
  (link: RegExp | string, title: string | RegExp): PageState['open'] =>
  async (page) => {
    await page.locator('main').getByRole('link', { name: link }).first().click();
    await heading(page, title);
  };

/** The first item of a list page (its main link), e.g. a built-in role. */
export const firstItem: PageState['open'] = async (page) => {
  const link = page.locator('main a.cell-title').first();
  const name = ((await link.textContent()) ?? '').trim();
  await link.click();
  await heading(page, name);
};

/** Opens the audit log and expands the first entry. */
export const auditExpanded: PageState['open'] = async (page) => {
  await nav('Audit log', 'Audit log')(page);
  await page.locator('table.audit button[aria-expanded="false"]').first().click();
};

/** Opens the admin UI (a full load, which signs in to it) at the dashboard. */
export async function openAdmin(page: Page): Promise<void> {
  await page.goto('/admin/');
  await heading(page, 'Dashboard');
}

/**
 * Signs out of the admin UI (and the account) and waits for its signed-out page. Signs in first, so it can be repeated;
 * every later state needs signIn again.
 */
export async function adminSignedOut(page: Page): Promise<void> {
  await signIn(page);
  await openAdmin(page);
  await page.getByRole('button', { name: 'Sign out' }).first().click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.getByText('You have signed out.').waitFor();
  await page.waitForLoadState('networkidle');
}
