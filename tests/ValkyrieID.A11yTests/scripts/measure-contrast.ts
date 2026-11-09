// Measures the text and icon contrast on the rendered admin and account pages (WCAG 2.2 1.4.3, 1.4.11), over the
// wallpaper and the glass: for every text node, generated text, and icon it hides the text and icons through the
// CSSOM, takes a screenshot, and takes the worst backdrop pixel under each one. Light and dark, at 1440x900,
// 1920x1080, 390 and 320 CSS px; on the pages and on states with messages (errors, notices), open confirmation
// dialogs, the admin UI's signed-out page, and a sign-in error. Run against a running test server
// (node scripts/serve.ts, which configures email, so /account/email is measured too); see
// docs/accessibility/index.md. Exits non-zero when anything is below 4.5:1 (text) or 3:1 (large text and icons).
import { chromium, type Page } from '@playwright/test';
import {
  adminSignedOut,
  auditExpanded,
  firstItem,
  nav,
  openAdmin,
  row,
  signIn,
  type PageState,
} from '../support/admin-session.ts';
import { baseUrl } from '../support/env.ts';
import { decodePng, type Image } from '../support/png.ts';
import { admin } from '../support/users.ts';

const sizes = [
  { width: 1440, height: 900 },
  { width: 1920, height: 1080 },
  { width: 390, height: 844 },
  { width: 320, height: 640 },
];
const themes = ['light', 'dark'] as const;
const firstSize = { width: 1440, height: 900 };

type Kind = 'text' | 'large text' | 'icon';

/** A box in viewport coordinates (CSS px). */
interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** What to measure, collected in the page while the text is visible. */
interface Target {
  kind: Kind;
  label: string;
  /** The foreground color (sRGB 0-255) and its alpha, including the ancestors' opacity. */
  color: [number, number, number, number];
  /** The opaque background of generated content with its own background (e.g. a badge mark), if any. */
  solid: [number, number, number] | null;
}

interface Result {
  page: string;
  theme: string;
  size: string;
  kind: Kind;
  label: string;
  ratio: number;
  minimum: number;
}

// --- In-page code (serialized by page.evaluate, so it can't use anything from this module). ---

/**
 * Collects the targets in document order; with `rects`, returns each one's visible boxes in viewport coordinates
 * instead (called while the text is hidden, when the colors are transparent).
 */
function inPage(rects: boolean): Target[] | Box[][] {
  const parse = (value: string): [number, number, number, number] => {
    // rgb()/rgba(), or color(srgb r g b / a) for color-mix() results.
    const srgb = /^color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: \/ ([\d.]+))?\)$/.exec(value);
    if (srgb) {
      return [
        Number(srgb[1]) * 255,
        Number(srgb[2]) * 255,
        Number(srgb[3]) * 255,
        srgb[4] === undefined ? 1 : Number(srgb[4]),
      ];
    }

    const m = /^rgba?\(([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+%?))?\)$/.exec(value);
    if (!m) {
      throw new Error(`Unsupported color "${value}".`);
    }

    const alpha =
      m[4] === undefined ? 1 : m[4].endsWith('%') ? parseFloat(m[4]) / 100 : parseFloat(m[4]);
    return [Number(m[1]), Number(m[2]), Number(m[3]), alpha];
  };
  const transparent: [number, number, number, number] = [0, 0, 0, 0];
  const colorOf = (value: string) => (rects ? transparent : parse(value));

  // Skipped: not rendered, visually hidden or clipped away (WCAG applies to visible content), disabled controls
  // (1.4.3 exception), aria-hidden text, which is the logotype (the wordmark; 1.4.3 exception), and the page behind an
  // open modal dialog, which is inert (inactive, 1.4.3 exception) and dimmed by the backdrop on purpose.
  const modal = document.querySelector('dialog:modal');
  const hidden = (element: Element): boolean => {
    if (modal && !modal.contains(element)) {
      return true;
    }

    for (let e: Element | null = element; e; e = e.parentElement) {
      const style = getComputedStyle(e);
      if (style.display === 'none' || style.visibility !== 'visible' || style.opacity === '0') {
        return true;
      }

      if (
        e.classList.contains('visually-hidden') ||
        /rect\(0(px)?,? 0(px)?,? 0(px)?,? 0(px)?\)/.test(style.clip)
      ) {
        return true;
      }

      if (style.clipPath !== 'none' && style.clipPath !== '') {
        return true;
      }

      const box = e.getBoundingClientRect();
      if (style.overflow !== 'visible' && (box.width <= 1 || box.height <= 1)) {
        return true;
      }
    }

    return false;
  };
  const disabled = (element: Element): boolean =>
    element.closest(':disabled, [aria-disabled="true"]') !== null;
  const opacity = (element: Element): number => {
    let value = 1;
    for (let e: Element | null = element; e; e = e.parentElement) {
      value *= parseFloat(getComputedStyle(e).opacity);
    }

    return value;
  };
  // The part of a box that isn't clipped by a scrolling or overflow-hidden ancestor, or by the viewport.
  const visible = (element: Element, box: Box): Box | null => {
    let left = Math.max(box.x, 0);
    let top = Math.max(box.y, 0);
    let right = Math.min(box.x + box.width, innerWidth);
    let bottom = Math.min(box.y + box.height, innerHeight);
    for (let e = element.parentElement; e; e = e.parentElement) {
      const style = getComputedStyle(e);
      if (style.overflowX !== 'visible' || style.overflowY !== 'visible') {
        const clip = e.getBoundingClientRect();
        left = Math.max(left, clip.left);
        top = Math.max(top, clip.top);
        right = Math.min(right, clip.right);
        bottom = Math.min(bottom, clip.bottom);
      }
    }

    return right - left >= 1 && bottom - top >= 1
      ? { x: left, y: top, width: right - left, height: bottom - top }
      : null;
  };

  const targets: Target[] = [];
  const boxes: Box[][] = [];
  const add = (target: Target, element: Element, list: Box[]) => {
    targets.push(target);
    boxes.push(list.map((b) => visible(element, b)).filter((b): b is Box => b !== null));
  };
  const large = (style: CSSStyleDeclaration) => {
    const size = parseFloat(style.fontSize);
    return size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700);
  };

  const walker = document.createTreeWalker(
    document.body,
    NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT,
  );
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node instanceof Text) {
      const parent = node.parentElement;
      if (
        !parent ||
        !node.data.trim() ||
        hidden(parent) ||
        disabled(parent) ||
        parent.closest('[aria-hidden="true"]')
      ) {
        continue;
      }

      if (parent.closest('script, style, template, option, title')) {
        continue;
      }

      const style = getComputedStyle(parent);
      const color = colorOf(style.color);

      const range = document.createRange();
      range.selectNodeContents(node);
      color[3] *= opacity(parent);
      add(
        {
          kind: large(style) ? 'large text' : 'text',
          label: node.data.trim().slice(0, 40),
          color,
          solid: null,
        },
        parent,
        [...range.getClientRects()].filter((r) => r.width > 0 && r.height > 0),
      );
      continue;
    }

    const element = node as Element;
    if (hidden(element) || disabled(element)) {
      continue;
    }

    if (element instanceof SVGSVGElement) {
      // Icons set stroke or fill (usually currentColor) on their shapes; the <svg> itself keeps the defaults.
      const shape = [
        ...element.querySelectorAll('path, circle, rect, line, polyline, polygon, ellipse'),
      ]
        .map((s) => getComputedStyle(s))
        .find((s) => s.stroke !== 'none' || s.fill !== 'none');
      const paint = shape
        ? shape.stroke !== 'none'
          ? shape.stroke
          : shape.fill
        : 'rgba(0, 0, 0, 0)';
      const color = colorOf(paint);
      color[3] *= opacity(element);
      const label =
        element.closest('[aria-label], button, a')?.getAttribute('aria-label') ?? 'icon';
      add({ kind: 'icon', label: `[icon] ${label}`.slice(0, 40), color, solid: null }, element, [
        element.getBoundingClientRect(),
      ]);
      continue;
    }

    // Generated content (badge marks, stacked table labels, the back arrow), measured against its element's opaque
    // background, or else over the element's box inside its borders and rounded corners (the pseudo-element's own
    // box isn't exposed). A single symbol (✓ ! – ○ ◆ ⊘ ⚠ › ←) is a graphic that supplements the text: 3:1 (1.4.11).
    for (const pseudo of ['::before', '::after']) {
      const style = getComputedStyle(element, pseudo);
      if (
        ['none', 'normal', '""', "'' / ''", '"" / ""'].includes(style.content) ||
        style.content.startsWith('url(')
      ) {
        continue;
      }

      if (style.display === 'none' || style.visibility !== 'visible') {
        continue;
      }

      const own = getComputedStyle(element);
      const color = colorOf(style.color);
      const pseudoBackground = rects ? transparent : parse(style.backgroundColor);
      const elementBackground = rects ? transparent : parse(own.backgroundColor);
      const background = pseudoBackground[3] === 1 ? pseudoBackground : elementBackground;
      color[3] *= opacity(element);
      const solid =
        background[3] === 1
          ? ([background[0], background[1], background[2]] as [number, number, number])
          : null;
      const text =
        element.getAttribute('data-label') ?? style.content.replace(/^"|" \/ ""$|"$/g, '');
      const symbol =
        !element.hasAttribute('data-label') &&
        [...text.replace(/[\s\uFE0E]/g, '')].length === 1 &&
        !/[\p{L}\p{N}]/u.test(text);
      const box = element.getBoundingClientRect();
      const inset = (side: string) => parseFloat(own.getPropertyValue(`border-${side}-width`));
      const radius = Math.min(
        parseFloat(own.borderTopLeftRadius) || 0,
        box.height / 2,
        box.width / 2,
      );
      const px = (value: string) => (value.endsWith('px') ? parseFloat(value) : NaN);
      // An absolutely positioned pseudo-element inside its (positioned) element, e.g. a stacked table label: its
      // own box, from the resolved left, top, width, and height.
      const placed =
        style.position === 'absolute' &&
        own.position !== 'static' &&
        [style.left, style.top, style.width].every((v) => !isNaN(px(v)));
      const height = px(style.height);
      const inner = placed
        ? {
            x: box.x + inset('left') + px(style.left),
            y: box.y + inset('top') + px(style.top),
            width: px(style.width),
            height: isNaN(height) ? parseFloat(style.fontSize) * 1.5 : height,
          }
        : {
            x: box.x + inset('left') + radius,
            y: box.y + inset('top'),
            width: box.width - inset('left') - inset('right') - 2 * radius,
            height: box.height - inset('top') - inset('bottom'),
          };
      add(
        {
          kind: symbol ? 'icon' : large(style) ? 'large text' : 'text',
          label: `[${pseudo}] ${text}`.slice(0, 40),
          color,
          solid,
        },
        element,
        inner.width > 0 && inner.height > 0 ? [inner] : [],
      );
    }
  }

  return rects ? boxes : targets;
}

// --- Contrast (WCAG 2.2 relative luminance and contrast ratio). ---

const linear = (channel: number) => {
  const c = channel / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
};
const luminance = (r: number, g: number, b: number) =>
  0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
const ratio = (a: number, b: number) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);

function worst(target: Target, image: Image, boxes: Box[]): number {
  const [r, g, b, alpha] = target.color;
  const contrast = (br: number, bg: number, bb: number) => {
    // A translucent foreground is drawn over the backdrop.
    const fr = alpha * r + (1 - alpha) * br;
    const fg = alpha * g + (1 - alpha) * bg;
    const fb = alpha * b + (1 - alpha) * bb;
    return ratio(luminance(fr, fg, fb), luminance(br, bg, bb));
  };

  if (target.solid) {
    return contrast(...target.solid);
  }

  let lowest = Infinity;
  for (const box of boxes) {
    const x0 = Math.max(0, Math.floor(box.x));
    const y0 = Math.max(0, Math.floor(box.y));
    const x1 = Math.min(image.width, Math.ceil(box.x + box.width));
    const y1 = Math.min(image.height, Math.ceil(box.y + box.height));
    for (let y = y0; y < y1; y++) {
      for (let x = x0; x < x1; x++) {
        const i = (y * image.width + x) * 4;
        lowest = Math.min(
          lowest,
          contrast(image.data[i] ?? 0, image.data[i + 1] ?? 0, image.data[i + 2] ?? 0),
        );
      }
    }
  }

  return lowest;
}

// --- Hiding text and icons through the CSSOM (the CSP allows no <style> elements), and restoring. ---

const hideRules = [
  `*, *::before, *::after, *::placeholder { color: transparent !important; -webkit-text-fill-color: transparent !important; text-shadow: none !important; text-decoration-color: transparent !important; caret-color: transparent !important; transition: none !important; animation: none !important; }`,
  `svg, svg * { fill: transparent !important; stroke: transparent !important; }`,
];

async function hide(page: Page): Promise<void> {
  await page.evaluate((rules) => {
    const sheet = [...document.styleSheets].find((s) => s.href?.startsWith(location.origin));
    if (!sheet) {
      throw new Error('No same-origin stylesheet to add the rules to.');
    }

    for (const rule of rules) {
      sheet.insertRule(rule, sheet.cssRules.length);
    }
  }, hideRules);
}

async function restore(page: Page): Promise<void> {
  await page.evaluate((count) => {
    const sheet = [...document.styleSheets].find((s) => s.href?.startsWith(location.origin));
    for (let i = 0; i < count && sheet; i++) {
      sheet.deleteRule(sheet.cssRules.length - 1);
    }
  }, hideRules.length);
}

/** Waits until the theme's color transitions and any entry animation have finished. */
async function settled(page: Page): Promise<void> {
  await page.evaluate(() => Promise.allSettled(document.getAnimations().map((a) => a.finished)));
  await page.waitForTimeout(150);
}

/** Measures the current page in its current theme and size, scrolling through it one viewport at a time. */
async function measure(page: Page, name: string, theme: string, size: string): Promise<Result[]> {
  await page.mouse.move(0, 0);
  await page.evaluate(() => {
    (document.activeElement as HTMLElement | null)?.blur();
    window.scrollTo(0, 0);
  });
  await settled(page);
  const targets = (await page.evaluate(inPage, false)) as Target[];
  const lowest = targets.map(() => Infinity);

  await hide(page);
  try {
    const { scrollHeight, viewport } = await page.evaluate(() => ({
      scrollHeight: document.documentElement.scrollHeight,
      viewport: innerHeight,
    }));
    for (let top = 0; ; top += Math.max(viewport - 120, 100)) {
      const scrolled = Math.min(top, Math.max(scrollHeight - viewport, 0));
      await page.evaluate((y) => window.scrollTo(0, y), scrolled);
      await page.waitForTimeout(50);
      const boxes = (await page.evaluate(inPage, true)) as {
        x: number;
        y: number;
        width: number;
        height: number;
      }[][];
      if (boxes.length !== targets.length) {
        throw new Error(
          `${name}: the page changed while measuring (${boxes.length} vs ${targets.length} targets).`,
        );
      }

      const image = decodePng(await page.screenshot({ animations: 'disabled', caret: 'hide' }));
      targets.forEach((target, i) => {
        const found = boxes[i] ?? [];
        if (found.length > 0) {
          lowest[i] = Math.min(lowest[i] ?? Infinity, worst(target, image, found));
        }
      });
      if (scrolled >= scrollHeight - viewport) {
        break;
      }
    }
  } finally {
    await restore(page);
    await page.evaluate(() => window.scrollTo(0, 0));
  }

  return targets.flatMap((target, i) => {
    const ratio = lowest[i] ?? Infinity;
    return Number.isFinite(ratio)
      ? [
          {
            page: name,
            theme,
            size,
            kind: target.kind,
            label: target.label,
            ratio,
            minimum: target.kind === 'text' ? 4.5 : 3,
          },
        ]
      : [];
  });
}

// --- The pages. Admin pages are reached by in-app navigation: every reload signs in to the admin UI again. ---

/** Opens the confirmation dialog of a button on the current page. */
const dialog =
  (button: string): PageState['open'] =>
  async (page) => {
    await page.locator('main').getByRole('button', { name: button, exact: true }).click();
    await page.locator('dialog[open]').waitFor();
  };
/** Closes an open dialog first. */
const closing =
  (open: PageState['open']): PageState['open'] =>
  async (page) => {
    await page.keyboard.press('Escape');
    await page.locator('dialog[open]').waitFor({ state: 'detached' });
    await open(page);
  };

const adminStates: PageState[] = [
  { name: 'admin dashboard', open: nav('Dashboard', 'Dashboard') },
  { name: 'admin users', open: nav('Users', 'Users') },
  {
    name: 'admin new user (errors)',
    open: async (page) => {
      await row('New user', 'New user')(page);
      await page.getByLabel('Username').fill('not valid!');
      await page.getByLabel('Password').fill('short');
      await page.getByRole('button', { name: 'Create user' }).click();
      await page.getByRole('alert').waitFor();
    },
  },
  {
    name: 'admin user detail (saved notice)',
    open: async (page) => {
      await nav('Users', 'Users')(page);
      await row('alice', 'alice')(page);
      await page.getByRole('button', { name: 'Save profile' }).click();
      await page.getByText('The profile was saved.').waitFor();
    },
  },
  { name: 'admin user detail (disable dialog)', open: dialog('Disable user') },
  { name: 'admin roles', open: closing(nav('Roles', 'Roles')) },
  { name: 'admin role detail', open: firstItem },
  { name: 'admin groups', open: nav('Groups', 'Groups') },
  { name: 'admin clients', open: nav('Clients', 'Clients') },
  { name: 'admin client detail', open: row('webapp', /Web app|webapp/) },
  { name: 'admin API resources', open: nav('API resources', 'API resources') },
  { name: 'admin API resource detail', open: row('orders', /Orders API|orders/) },
  { name: 'admin signing keys', open: nav('Signing keys', 'Signing keys') },
  { name: 'admin signing keys (rotate dialog)', open: dialog('Rotate now') },
  {
    name: 'admin audit log (entry expanded)',
    open: closing(auditExpanded),
  },
  { name: 'admin settings', open: nav('Settings', 'Settings') },
  { name: 'admin settings section', open: row(/Token lifetimes/, 'Token lifetimes') },
  // Last: signs out (of the account too).
  { name: 'admin signed out', open: adminSignedOut },
  {
    name: 'sign-in error',
    open: async (page) => {
      await page.goto('/account/login');
      // An unknown user: the same generic error, without counting failures against the administrator (lockout).
      await page.getByLabel('Username or email').fill('nobody');
      await page.getByLabel('Password').fill('not the password');
      await page.getByRole('button', { name: 'Sign in' }).click();
      await page.getByRole('alert').first().waitFor();
    },
  },
];

/** Account pages, by path, with an optional action that leads to a message (an error or a notice). */
const accountPages: [string, string, ((page: Page) => Promise<void>)?][] = [
  ['account', '/account'],
  ['account profile', '/account/profile'],
  [
    'account profile (saved notice)',
    '/account/profile',
    async (page) => {
      await page.getByRole('button', { name: 'Save' }).click();
      await page.locator('[role="status"].notice').waitFor();
    },
  ],
  ['account email', '/account/email'],
  ['account password', '/account/password'],
  [
    'account password (errors)',
    '/account/password',
    async (page) => {
      // The correct current password (a wrong one would count toward the lockout), and an invalid new one.
      await page.getByLabel('Current password').fill(admin.password);
      await page.getByLabel('New password', { exact: true }).fill('short');
      await page.getByLabel('Confirm new password').fill('different');
      await page.getByRole('button', { name: 'Change password' }).click();
      await page.getByRole('alert').first().waitFor();
    },
  ],
  ['account two-factor (danger zone)', '/account/two-factor'],
  ['account sessions', '/account/sessions'],
];

// --- Run. ---

const browser = await chromium.launch();
const context = await browser.newContext({
  baseURL: baseUrl,
  ignoreHTTPSErrors: true,
  viewport: sizes[0],
});
const page = await context.newPage();
const results: Result[] = [];

async function everySizeAndTheme(name: string): Promise<void> {
  for (const size of sizes) {
    await page.setViewportSize(size);
    for (const theme of themes) {
      await page.emulateMedia({ colorScheme: theme });
      const found = await measure(page, name, theme, `${size.width}x${size.height}`);
      results.push(...found);
      const low = found.filter((r) => r.ratio < r.minimum).length;
      console.log(
        `${name}, ${theme}, ${size.width}x${size.height}: ${found.length} measured${low ? `, ${low} below the minimum` : ''}`,
      );
    }
  }
}

try {
  await signIn(page);
  for (const [name, path, action] of accountPages) {
    await page.setViewportSize(firstSize);
    // The email page exists only when email is configured.
    const response = await page.goto(path);
    if (response?.status() === 404) {
      console.log(`${name}: skipped (${path} isn't available on this server)`);
      continue;
    }

    await action?.(page);
    await everySizeAndTheme(name);
  }

  await page.setViewportSize(firstSize);
  await openAdmin(page);
  for (const state of adminStates) {
    await page.setViewportSize(firstSize);
    await state.open(page);
    await everySizeAndTheme(state.name);
  }
} finally {
  await browser.close();
}

const format = (r: Result) =>
  `${r.ratio.toFixed(2).padStart(6)}:1  ${r.kind.padEnd(10)}  ${r.page}, ${r.theme}, ${r.size}: "${r.label}"`;
console.log('\nLowest per kind:');
for (const kind of ['text', 'large text', 'icon'] as const) {
  const lowest = results.filter((r) => r.kind === kind).sort((a, b) => a.ratio - b.ratio)[0];
  console.log(lowest ? format(lowest) : `(no ${kind})`);
}

const failures = results.filter((r) => r.ratio < r.minimum).sort((a, b) => a.ratio - b.ratio);
if (failures.length > 0) {
  console.log(
    `\n${failures.length} below the minimum (4.5:1 text, 3:1 large text and icons), lowest per page, theme and label:`,
  );
  const seen = new Map<string, { worst: Result; sizes: Set<string>; count: number }>();
  for (const r of failures) {
    const key = `${r.page}|${r.theme}|${r.kind}|${r.label}`;
    const entry = seen.get(key) ?? { worst: r, sizes: new Set<string>(), count: 0 };
    entry.sizes.add(r.size);
    entry.count++;
    seen.set(key, entry);
  }

  for (const { worst, sizes: at, count } of seen.values()) {
    console.log(
      `${format({ ...worst, size: [...at].join(' ') })}${count > at.size ? ` (${count}x)` : ''}`,
    );
  }

  process.exit(1);
}

console.log(`\nAll ${results.length} measurements meet the minimum.`);
