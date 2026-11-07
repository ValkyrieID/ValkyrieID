import type { Page } from '@playwright/test';
import { checkPage, expect, tabThrough, test } from '../support/a11y.ts';
import { createUser, submitLogin } from '../support/users.ts';

/** Opens an admin UI route (a full load: the admin UI signs in silently through the SSO session). */
async function open(page: Page, path: string, heading: string | RegExp): Promise<void> {
  await page.goto(`/admin${path}`);
  await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await expect(page.getByText('Loading…')).toHaveCount(0);
}

/** Opens every confirmation dialog on the page, checks it, and closes it with Escape. */
async function checkDialogs(page: Page, name: string): Promise<void> {
  const buttons = page.locator('app-confirm-button > button:not([disabled])');
  const count = await buttons.count();
  for (let i = 0; i < count; i++) {
    const button = buttons.nth(i);
    const label = (await button.textContent())?.trim() ?? '';
    await button.click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    // The safe choice gets the focus first.
    await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
    await checkPage(page, `${name}: "${label}" dialog`);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    // Closing returns the focus to the button that opened it (2.4.3).
    await expect(button).toBeFocused();
  }
}

test.describe.configure({ mode: 'serial' });

test('dashboard and navigation', async ({ page }) => {
  await open(page, '/', 'Dashboard');
  await expect(page).toHaveTitle(/^Dashboard – /);
  await checkPage(page, 'dashboard');

  // The skip link comes first and moves the focus to the main content (2.4.1).
  const [first] = await tabThrough(page, 1);
  expect(first).toBe('Skip to content');
  await page.keyboard.press('Enter');
  await expect(page.locator('main#main')).toBeFocused();

  // Client-side navigation moves the focus to the new page, like a page load (2.4.3).
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Users' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Users' })).toBeVisible();
  await expect(page.locator('main#main')).toBeFocused();
  await expect(
    page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Users' }),
  ).toHaveAttribute('aria-current', 'page');
});

test('users', async ({ page }) => {
  await open(page, '/users', 'Users');
  await checkPage(page, 'users');
  await page.getByLabel('Search by username or email').fill('no-such-user');
  await page.getByRole('button', { name: 'Search' }).click();
  await expect(page.getByText('No users found.')).toBeVisible();
  await checkPage(page, 'users, none found');

  await open(page, '/users/new', 'New user');
  await checkPage(page, 'new user');
  await page.getByLabel('Username').fill('not valid!');
  await page.getByLabel('Password').fill('short');
  await page.getByRole('button', { name: 'Create user' }).click();
  await expect(page.getByRole('alert')).toBeFocused();
  await checkPage(page, 'new user with errors');

  await page.getByLabel('Username').fill('erin');
  await page.getByLabel('Password').fill('correct horse battery staple erin');
  await page.getByLabel('Email').fill('erin@example.test');
  await page.getByRole('button', { name: 'Create user' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'erin' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await checkPage(page, 'user');
  await checkDialogs(page, 'user');

  await open(page, '/users/00000000-0000-0000-0000-000000000000', 'User');
  await checkPage(page, 'unknown user');
});

test('roles and groups', async ({ page }) => {
  for (const [path, title, singular] of [
    ['/roles', 'Roles', 'role'],
    ['/groups', 'Groups', 'group'],
  ] as const) {
    await open(page, path, title);
    await checkPage(page, title.toLowerCase());
    await page.getByLabel('Search').fill('no-such-name');
    await page.getByRole('button', { name: 'Search' }).click();
    await expect(page.getByText('None found.')).toBeVisible();
    await checkPage(page, `${title.toLowerCase()}, none found`);

    await page.getByLabel('Name', { exact: true }).fill(`support-${singular}`);
    await page.getByRole('button', { name: 'Create' }).click();
    await page.getByLabel('Search').fill('');
    await page.getByRole('button', { name: 'Search' }).click();
    await page.getByRole('link', { name: `support-${singular}` }).click();
    await expect(
      page.getByRole('heading', { level: 1, name: `support-${singular}` }),
    ).toBeVisible();
    await page.waitForLoadState('networkidle');
    await checkPage(page, singular);
    await checkDialogs(page, singular);

    await page.goto(`/admin${path}`);
    await page.getByLabel('Name', { exact: true }).fill(`support-${singular}`);
    await page.getByRole('button', { name: 'Create' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await checkPage(page, `new ${singular} with errors`);
  }
});

test('clients', async ({ page }) => {
  await open(page, '/clients', 'Clients');
  await checkPage(page, 'clients');
  await page.getByLabel('Search by client id or name').fill('no-such-client');
  await page.getByRole('button', { name: 'Search' }).click();
  await expect(page.getByText('No clients found.')).toBeVisible();
  await checkPage(page, 'clients, none found');

  await open(page, '/clients/new', 'New client');
  await checkPage(page, 'new client');
  await page.getByRole('button', { name: /^Create/ }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await checkPage(page, 'new client with errors');

  await open(page, '/clients', 'Clients');
  await page.getByRole('link', { name: 'webapp' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Web app' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await checkPage(page, 'client');
  await checkDialogs(page, 'client');

  await open(page, '/clients/00000000-0000-0000-0000-000000000000', 'Client');
  await checkPage(page, 'unknown client');
});

test('API resources', async ({ page }) => {
  await open(page, '/resources', 'API resources');
  await checkPage(page, 'API resources');

  await open(page, '/resources/new', 'New API resource');
  await checkPage(page, 'new API resource');
  await page.getByRole('button', { name: /^Create/ }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await checkPage(page, 'new API resource with errors');

  await open(page, '/resources', 'API resources');
  await page.getByRole('link', { name: 'orders' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Orders API' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await checkPage(page, 'API resource');
  await checkDialogs(page, 'API resource');

  // A new secret is shown once.
  await page
    .getByRole('button', { name: /Add secret|New secret/ })
    .first()
    .click();
  await expect(page.getByTestId('secret')).toBeVisible();
  await checkPage(page, 'API resource with a new secret');
});

test('signing keys and audit log', async ({ page }) => {
  await open(page, '/signing-keys', 'Signing keys');
  await checkPage(page, 'signing keys');
  await checkDialogs(page, 'signing keys');

  await open(page, '/audit', 'Audit log');
  await checkPage(page, 'audit log');
  await page.getByLabel('Actor').fill('nobody');
  await page.getByRole('button', { name: 'Apply filters' }).click();
  await expect(page.getByText('No entries.')).toBeVisible();
  await checkPage(page, 'audit log, no entries');
});

test('settings', async ({ page }) => {
  await open(page, '/settings', 'Settings');
  await checkPage(page, 'settings');
  const sections = await page
    .locator('.settings-list a')
    .evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
  expect(sections.length).toBeGreaterThan(0);
  for (const href of sections) {
    await page.goto(href);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await page.waitForLoadState('networkidle');
    await checkPage(page, `settings ${href}`);
  }
});

test('not found', async ({ page }) => {
  await open(page, '/no-such-page', 'Page not found');
  await checkPage(page, 'not found');
});

test('sign-in error', async ({ page }) => {
  // RFC 9207: a response from another issuer is refused.
  await page.goto('/admin/callback?code=x&state=x&iss=https%3A%2F%2Fevil.test');
  await expect(page.getByRole('alert')).toBeVisible();
  await checkPage(page, 'sign-in error');
});

test('not an admin', async ({ browser }) => {
  const user = await createUser('frank');
  const context = await browser.newContext({ storageState: { cookies: [], origins: [] } });
  const page = await context.newPage();
  await page.goto('/admin/');
  await submitLogin(page, user.userName, user.password);
  await expect(page.getByRole('heading', { level: 1, name: 'Not authorized' })).toBeVisible();
  await checkPage(page, 'not authorized');
  await context.close();
});

test('signed out', async ({ page }) => {
  await open(page, '/', 'Dashboard');
  await page.getByRole('button', { name: 'Sign out' }).first().click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByText('You have signed out.')).toBeVisible();
  await checkPage(page, 'signed out');
});
