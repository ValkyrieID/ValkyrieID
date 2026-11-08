import type { Page } from '@playwright/test';
import { checkPage, expect, test } from '../support/a11y.ts';

// The project runs with prefers-color-scheme: dark. Read-only routes only: admin-ui.spec.ts creates the data.

async function open(page: Page, path: string, heading: string): Promise<void> {
  await page.goto(`/admin${path}`);
  await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  await page.waitForLoadState('networkidle');
}

/** Color transitions run when the theme changes in place; check the final colors. */
async function settled(page: Page): Promise<void> {
  await page.waitForFunction(() => document.getAnimations().length === 0);
}

test.describe.configure({ mode: 'serial' });

test('admin UI starts dark, so it never flashes light', async ({ request }) => {
  const html = await (await request.get('/admin/')).text();

  expect(html).toContain('<html lang="en" data-theme="dark">');
});

for (const [path, heading] of [
  ['/', 'Dashboard'],
  ['/users', 'Users'],
  ['/clients', 'Clients'],
  ['/audit', 'Audit log'],
  ['/settings', 'Settings'],
] as const) {
  test(`${heading}, dark theme (system)`, async ({ page }) => {
    await open(page, path, heading);
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'system');
    await checkPage(page, `${heading} (dark)`);
  });
}

test('theme switch', async ({ page }) => {
  await open(page, '/', 'Dashboard');
  const group = page.getByRole('group', { name: 'Color theme' });
  const html = page.locator('html');

  await group.getByRole('button', { name: 'Light' }).click();
  await expect(html).toHaveAttribute('data-theme', 'light');
  await expect(group.getByRole('button', { name: 'Light' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await settled(page);
  await checkPage(page, 'dashboard, explicit light theme with a dark system');

  // Shared with the login pages through the cookie.
  await page.goto('/account');
  await expect(html).toHaveAttribute('data-theme', 'light');

  await open(page, '/', 'Dashboard');
  await expect(html).toHaveAttribute('data-theme', 'light');
  await group.getByRole('button', { name: 'Dark' }).click();
  await expect(html).toHaveAttribute('data-theme', 'dark');
  await settled(page);
  await checkPage(page, 'dashboard, explicit dark theme');
  await group.getByRole('button', { name: 'System' }).click();
  await expect(html).toHaveAttribute('data-theme', 'system');
});
