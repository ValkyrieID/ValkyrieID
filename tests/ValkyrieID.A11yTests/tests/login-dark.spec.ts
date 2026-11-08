import { checkPage, expect, test } from '../support/a11y.ts';
import { defineLoginPageTests } from '../support/login-suite.ts';

// The project runs with prefers-color-scheme: dark, so "System" (the default) shows the dark theme.

test.describe('login pages, dark theme (system)', () => {
  defineLoginPageTests('dark');
});

test.describe('theme switch', () => {
  test('follows the system, and keeps an explicit choice without JavaScript', async ({ page }) => {
    await page.goto('/account/login');
    const html = page.locator('html');
    const group = page.getByRole('group', { name: 'Color theme' });
    await expect(html).toHaveAttribute('data-theme', 'system');
    await expect(group.getByRole('button', { name: 'System' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await group.getByRole('button', { name: 'Light' }).click();
    await expect(page).toHaveURL(/\/account\/login$/);
    await expect(html).toHaveAttribute('data-theme', 'light');
    await expect(group.getByRole('button', { name: 'Light' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await checkPage(page, 'sign-in, explicit light theme with a dark system');

    await group.getByRole('button', { name: 'Dark' }).click();
    await expect(html).toHaveAttribute('data-theme', 'dark');
    await checkPage(page, 'sign-in, explicit dark theme');

    // The choice applies to the other pages too.
    await page.goto('/account/forgot-password');
    await expect(html).toHaveAttribute('data-theme', 'dark');
  });

  test('works with JavaScript turned off', async ({ browser }) => {
    const context = await browser.newContext({ javaScriptEnabled: false, colorScheme: 'light' });
    const page = await context.newPage();
    await page.goto('/account/login');

    await page
      .getByRole('group', { name: 'Color theme' })
      .getByRole('button', { name: 'Dark' })
      .click();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await context.close();
  });
});
