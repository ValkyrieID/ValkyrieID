import type { Browser, Page } from '@playwright/test';
import { expect, test } from '../support/a11y.ts';
import { adminStorageState, baseUrl } from '../support/env.ts';

// Regression tests for Phase 11e (layout.md): theme preference, responsive tables, account navigation, and the audit
// log's details. Runs before the suites that sign the admin's stored session out.

test.describe.configure({ mode: 'serial' });

const themeCookie = '__Host-valkyrie.theme';

/** Whether the page renders dark, from its text color (light text on a dark background). */
async function rendersDark(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const [r, g, b] = (getComputedStyle(document.body).color.match(/[\d.]+/g) ?? []).map(Number);
    return 0.2126 * (r ?? 0) + 0.7152 * (g ?? 0) + 0.0722 * (b ?? 0) > 128;
  });
}

async function newPage(
  browser: Browser,
  options: {
    colorScheme: 'light' | 'dark';
    theme?: string;
    javaScriptEnabled?: boolean;
    admin?: boolean;
  },
): Promise<Page> {
  const context = await browser.newContext({
    baseURL: baseUrl,
    ignoreHTTPSErrors: true,
    colorScheme: options.colorScheme,
    javaScriptEnabled: options.javaScriptEnabled ?? true,
    ...(options.admin ? { storageState: adminStorageState } : {}),
  });
  if (options.theme !== undefined) {
    await context.addCookies([
      { name: themeCookie, value: options.theme, url: baseUrl, secure: true, sameSite: 'Lax' },
    ]);
  }

  return context.newPage();
}

async function openAdmin(page: Page, path: string, heading: string | RegExp): Promise<void> {
  await page.goto(`/admin${path}`);
  await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await expect(page.getByText('Loading…')).toHaveCount(0);
}

test.describe('theme preference', () => {
  for (const colorScheme of ['light', 'dark'] as const) {
    test(`without a cookie the login pages follow the ${colorScheme} system theme`, async ({
      browser,
    }) => {
      const page = await newPage(browser, { colorScheme });
      await page.goto('/account/login');

      await expect(page.locator('html')).toHaveAttribute('data-theme', 'system');
      expect(await rendersDark(page)).toBe(colorScheme === 'dark');
      await expect(page.getByRole('button', { name: 'System theme' })).toHaveAttribute(
        'aria-pressed',
        'true',
      );
      await page.context().close();
    });
  }

  for (const [theme, colorScheme] of [
    ['dark', 'light'],
    ['light', 'dark'],
  ] as const) {
    test(`a ${theme} cookie wins over a ${colorScheme} system theme, without JavaScript`, async ({
      browser,
    }) => {
      // Rendered on the server: the first paint is already in the chosen theme, nothing is switched by a script.
      const page = await newPage(browser, { colorScheme, theme, javaScriptEnabled: false });
      await page.goto('/account/login');

      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      expect(await rendersDark(page)).toBe(theme === 'dark');
      await page.context().close();
    });
  }

  test('an unknown cookie value follows the system theme', async ({ browser }) => {
    const page = await newPage(browser, { colorScheme: 'dark', theme: 'purple' });
    await page.goto('/account/login');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'system');
    expect(await rendersDark(page)).toBe(true);
    await page.context().close();
  });

  test('the admin UI paints dark before its script runs, so it never flashes light', async ({
    browser,
  }) => {
    // A light system theme and no cookie: the page may go from dark to light, but not start light.
    const page = await newPage(browser, { colorScheme: 'light', javaScriptEnabled: false });
    await page.goto('/admin/');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    expect(await rendersDark(page)).toBe(true);
    await page.context().close();
  });

  test('the admin UI applies the cookie before the first render', async ({ browser }) => {
    const page = await newPage(browser, { colorScheme: 'dark', theme: 'light', admin: true });
    // The theme is applied when the app starts, before the router renders the dashboard.
    const seen: string[] = [];
    await page.exposeFunction('reportTheme', (value: string) => seen.push(value));
    await page.addInitScript(() => {
      new MutationObserver(() => {
        if (document.querySelector('h1')) {
          void (window as unknown as { reportTheme: (v: string) => void }).reportTheme(
            document.documentElement.getAttribute('data-theme') ?? '',
          );
        }
      }).observe(document, { subtree: true, childList: true });
    });
    await openAdmin(page, '/', 'Dashboard');

    expect(seen[0]).toBe('light');
    expect(await rendersDark(page)).toBe(false);
    await page.context().close();
  });
});

test.describe('responsive tables', () => {
  test.use({ storageState: adminStorageState, viewport: { width: 390, height: 844 } });

  test('stacked rows label each value and keep the table semantics', async ({ page }) => {
    await openAdmin(page, '/users', 'Users');
    const table = page.getByRole('table');

    // The header row is visually hidden but still names the columns for assistive technology.
    await expect(table.getByRole('columnheader', { name: 'User' })).toBeAttached();
    await expect(table.locator('thead')).toHaveCSS('position', 'absolute');
    const row = table.locator('tbody tr').first();
    await expect(row).toHaveAttribute('role', 'row');
    await expect(row).toHaveCSS('display', 'block');

    // Every cell shows its column name beside the value, as generated content without alternative text.
    const labels = await row
      .locator('td[data-label]')
      .evaluateAll((cells) =>
        cells.map((cell) => [
          cell.getAttribute('role'),
          cell.getAttribute('data-label'),
          getComputedStyle(cell, '::before').content,
        ]),
      );
    expect(labels.length).toBeGreaterThan(1);
    for (const [role, label, content] of labels) {
      expect(role).toBe('cell');
      expect(content).toBe(`"${label}" / ""`);
    }
  });

  for (const width of [390, 320]) {
    test(`the active signing key fits and its labels clear the marker at ${width} px`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 844 });
      await openAdmin(page, '/signing-keys', 'Signing keys');

      const wrap = page.locator('.table-wrap');
      const { scrollWidth, clientWidth } = await wrap.evaluate((e) => ({
        scrollWidth: e.scrollWidth,
        clientWidth: e.clientWidth,
      }));
      expect(scrollWidth, 'the stacked table scrolls sideways').toBeLessThanOrEqual(clientWidth);

      const row = page.locator('tr.row-active');
      const gap = await row.evaluate((tr) => {
        const marker = 3;
        const cell = tr.querySelector('td[data-label]');
        if (!cell) {
          return -1;
        }

        const label = getComputedStyle(cell, '::before');
        const left = cell.getBoundingClientRect().left + parseFloat(label.left);
        return left - (tr.getBoundingClientRect().left + marker);
      });
      expect(gap, 'the column labels sit on the green marker').toBeGreaterThanOrEqual(4);
    });
  }

  test('radio buttons and checkboxes keep their size on small screens', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await openAdmin(page, '/clients/new', 'New client');

    for (const control of await page.locator('input[type=radio], input[type=checkbox]').all()) {
      const box = await control.boundingBox();
      expect(box?.width).toBe(20);
      expect(box?.height).toBe(20);
    }
  });

  test('confirm dialogs keep a margin to the screen edges', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await openAdmin(page, '/signing-keys', 'Signing keys');
    await page.getByRole('button', { name: 'Rotate now' }).click();
    const box = await page.getByRole('dialog').boundingBox();

    expect(box?.x).toBeGreaterThanOrEqual(16);
    expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(320 - 16);
    await page.keyboard.press('Escape');
  });
});

test.describe('account navigation', () => {
  test.use({ storageState: adminStorageState });

  for (const [path, label] of [
    ['/account', 'Overview'],
    ['/account/profile', 'Profile'],
    ['/account/password', 'Password'],
    ['/account/two-factor', 'Two-factor'],
    ['/account/sessions', 'Sessions'],
  ] as const) {
    test(`${label} is the current page`, async ({ page }) => {
      await page.goto(path);
      const nav = page.getByRole('navigation', { name: 'Account' });

      await expect(nav.locator('[aria-current]')).toHaveCount(1);
      await expect(nav.getByRole('link', { name: label, exact: true })).toHaveAttribute(
        'aria-current',
        'page',
      );
    });
  }
});

test.describe('audit log details', () => {
  test.use({ storageState: adminStorageState });

  test('a disclosure button shows the details, copies an ID, and announces it', async ({
    page,
    context,
  }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: baseUrl });
    await openAdmin(page, '/audit', 'Audit log');
    const button = page.locator('table.audit button[aria-expanded]').first();

    await expect(button).toHaveAccessibleName(/^Details of [\w.]+, /);
    await expect(button).toHaveAttribute('aria-expanded', 'false');
    await expect(button).not.toHaveAttribute('aria-controls');

    await button.focus();
    await page.keyboard.press('Enter');
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    await expect(button).toBeFocused();
    const id = await button.getAttribute('aria-controls');
    const details = page.locator(`[id="${id}"]`);
    await expect(details).toBeVisible();
    await expect(details.locator('dt').first()).toHaveText('Time');

    // The copy button copies the full value and announces it politely, without moving the focus.
    const entryId = (
      await details.locator('dt:text-is("Entry ID") + dd code').textContent()
    )?.trim();
    const copy = details.getByRole('button', { name: 'Copy entry ID' });
    await copy.click();
    await expect(
      page.locator('[aria-live="polite"]').filter({ hasText: 'Entry ID copied.' }),
    ).toHaveCount(1);
    await expect(copy).toBeFocused();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(entryId);

    await button.focus();
    await page.keyboard.press('Enter');
    await expect(button).toHaveAttribute('aria-expanded', 'false');
    await expect(details).toHaveCount(0);
  });
});
