import AxeBuilder from '@axe-core/playwright';
import { test as base, expect, type Page } from '@playwright/test';
import { baseUrl } from './env.ts';

/** WCAG 2.0, 2.1 and 2.2 level A and AA; anything else axe checks (best practices) isn't a failure. */
export const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];

/**
 * Every test fails on a Content Security Policy violation: the login pages and the admin UI must keep working under
 * their strict CSP (no inline scripts or styles), so an accessibility fix can't quietly add one.
 */
export const test = base.extend<{ cspViolations: string[] }>({
  cspViolations: [
    async ({ page }, use) => {
      // Client redirect URIs in the tests point to an unused loopback port; answer them, so a flow can end there.
      await page
        .context()
        .route('http://127.0.0.1:8765/**', (route) =>
          route.fulfill({ contentType: 'text/plain', body: 'Returned to the application.' }),
        );
      const violations: string[] = [];
      page.on('console', (message) => {
        if (message.type() === 'error' && /Content Security Policy/i.test(message.text())) {
          violations.push(message.text());
        }
      });
      await use(violations);
      expect(violations, 'Content Security Policy violations').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };

/**
 * Checks the current page: axe (WCAG 2.2 A/AA), reflow at 320 CSS px (1.4.10, the width of 1280 px at 400% zoom), and
 * no animations or transitions with reduced motion (2.3.3).
 */
export async function checkPage(page: Page, name: string): Promise<void> {
  await test.step(`check ${name}`, async () => {
    // The entry animation fades the content in; check the page as it ends up, not half-faded.
    await page.evaluate(() => Promise.allSettled(document.getAnimations().map((a) => a.finished)));
    const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze();
    const violations = results.violations.map((violation) => ({
      rule: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes.map((node) => `${node.target.join(' ')}: ${node.failureSummary}`),
    }));
    expect.soft(violations, `axe violations on ${name}`).toEqual([]);

    await expectReflow(page, name);
    await expectReducedMotion(page, name);
  });
}

async function expectReflow(page: Page, name: string): Promise<void> {
  const size = page.viewportSize();
  await page.setViewportSize({ width: 320, height: 640 });
  try {
    // Data tables may scroll horizontally inside their own container (1.4.10 exception); the page itself must not.
    const overflow = await page.evaluate(() => {
      const width = document.documentElement.clientWidth;
      if (document.documentElement.scrollWidth <= width) {
        return [];
      }

      // To tell what to fix: the deepest element whose removal fixes the overflow, with its ancestors.
      const overflows = () => document.documentElement.scrollWidth > width;
      let culprit: Element = document.body;
      for (let found = true; found;) {
        found = false;
        for (const child of culprit.children) {
          const element = child as HTMLElement;
          const display = element.style.display;
          element.style.display = 'none';
          const fixed = !overflows();
          element.style.display = display;
          if (fixed) {
            culprit = child;
            found = true;
            break;
          }
        }
      }

      const chain: string[] = [];
      for (let e: Element | null = culprit; e && e !== document.body; e = e.parentElement) {
        chain.push(
          `${e.tagName.toLowerCase()}${e.className ? `.${String(e.className).replace(/\s+/g, '.')}` : ''} (${Math.round(e.getBoundingClientRect().width)}px)`,
        );
      }

      return [`${chain.join(' < ')}; page ${document.documentElement.scrollWidth}px`];
    });
    expect.soft(overflow, `content wider than 320 CSS px on ${name}`).toEqual([]);
  } finally {
    if (size) {
      await page.setViewportSize(size);
    }
  }
}

async function expectReducedMotion(page: Page, name: string): Promise<void> {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  try {
    const moving = await page.evaluate(() =>
      [...document.querySelectorAll('*')]
        .filter((element) => {
          const style = getComputedStyle(element);
          const seconds = (value: string) =>
            Math.max(...value.split(',').map((v) => parseFloat(v)));
          return (
            (style.animationName !== 'none' && seconds(style.animationDuration) > 0) ||
            seconds(style.transitionDuration) > 0
          );
        })
        .map((element) => element.tagName.toLowerCase() + (element.id ? `#${element.id}` : '')),
    );
    expect.soft(moving, `animated elements with reduced motion on ${name}`).toEqual([]);
  } finally {
    await page.emulateMedia({ reducedMotion: null });
  }
}

/**
 * Checks a page that only renders without JavaScript (axe itself needs JavaScript): serves a copy of its HTML, with
 * the scripts removed, from the test server's origin so its stylesheets load, and checks that.
 */
export async function checkCopy(page: Page, html: string, name: string): Promise<void> {
  const url = new URL('/__a11y-copy', page.url() === 'about:blank' ? baseUrl : page.url()).href;
  await page.route(url, (route) =>
    route.fulfill({
      contentType: 'text/html; charset=utf-8',
      body: html.replace(/<script\b[\s\S]*?<\/script>/gi, ''),
    }),
  );
  await page.goto(url);
  await checkPage(page, name);
  await page.unroute(url);
}

/**
 * Tabs through the page and checks that every focused element shows a focus indicator (2.4.7) that isn't hidden
 * (2.4.11). Returns the focused elements' names, so tests can check the order (2.4.3).
 */
export async function tabThrough(page: Page, steps: number): Promise<string[]> {
  const names: string[] = [];
  for (let i = 0; i < steps; i++) {
    await page.keyboard.press('Tab');
    const focused = await page.evaluate(() => {
      const element = document.activeElement;
      if (!element || element === document.body) {
        return null;
      }

      element.scrollIntoView({ block: 'nearest' });
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      const indicator =
        (style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2) ||
        style.boxShadow !== 'none';
      // 2.4.11: the element isn't entirely covered by something else (e.g. a sticky header).
      const top = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      const visible =
        top !== null && (element === top || element.contains(top) || top.contains(element));
      return {
        name:
          element.getAttribute('aria-label') ??
          (element.textContent?.trim() ||
            (element as HTMLInputElement).labels?.[0]?.textContent?.trim() ||
            element.tagName.toLowerCase()),
        indicator,
        visible,
      };
    });
    if (!focused) {
      break;
    }

    expect(focused.indicator, `focus indicator on "${focused.name}"`).toBe(true);
    expect(focused.visible, `"${focused.name}" not obscured when focused`).toBe(true);
    names.push(focused.name);
  }

  return names;
}
