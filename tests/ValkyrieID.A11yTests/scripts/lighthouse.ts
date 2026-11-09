// Runs Lighthouse (accessibility and best practices) on representative login, account, and admin pages and states, in
// light and dark, on desktop and mobile. Run against a running test server (node scripts/serve.ts); see
// docs/accessibility/index.md. Pages are loaded with a Lighthouse navigation; states that only exist after
// interaction (a sign-in error, an expanded audit entry) are audited as a snapshot of the current page. Reports go to
// test-results/lighthouse. Exits non-zero when any score is below 100.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium, type Page } from '@playwright/test';
import lighthouse, {
  defaultConfig,
  desktopConfig,
  generateReport,
  snapshot,
  type Config,
  type Flags,
  type Result,
} from 'lighthouse';
import puppeteer, { type Browser as PuppeteerBrowser } from 'puppeteer-core';
import { adminSignedOut, auditExpanded, openAdmin, signIn } from '../support/admin-session.ts';
import { baseUrl, packageDir } from '../support/env.ts';

const debuggingPort = Number(process.env['A11Y_DEBUGGING_PORT'] ?? 9333);
const outputDir = join(packageDir, 'test-results', 'lighthouse');
const categories = ['accessibility', 'best-practices'];
const themes = ['light', 'dark'] as const;
const devices = ['desktop', 'mobile'] as const;
type Device = (typeof devices)[number];

interface State {
  name: string;
  /** A path for a Lighthouse navigation, or a function that sets up the current page for a snapshot. */
  open: string | ((page: Page) => Promise<void>);
}

/** Signed out. */
const signedOut: State[] = [
  { name: 'sign-in', open: '/account/login' },
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

/** Signed in as the administrator. */
const signedIn: State[] = [
  { name: 'account', open: '/account' },
  { name: 'account sessions', open: '/account/sessions' },
  { name: 'admin dashboard', open: '/admin/' },
  {
    name: 'admin audit log (entry expanded)',
    open: async (page) => {
      await openAdmin(page);
      await auditExpanded(page);
    },
  },
  // Last: it ends the session (and signs in again for every run).
  { name: 'admin signed out', open: adminSignedOut },
];

interface Score {
  state: string;
  theme: string;
  device: Device;
  mode: 'navigation' | 'snapshot';
  scores: Record<string, number>;
  failing: string[];
}

const configFor = (device: Device): Config => ({
  ...(device === 'desktop' ? desktopConfig : defaultConfig),
  settings: {
    ...(device === 'desktop' ? desktopConfig.settings : defaultConfig.settings),
    onlyCategories: categories,
  },
});

/** The failing audits of the categories, with the elements they flag. */
function failingAudits(lhr: Result): string[] {
  const failing: string[] = [];
  for (const id of categories) {
    for (const ref of lhr.categories[id]?.auditRefs ?? []) {
      const audit = lhr.audits[ref.id];
      if (!audit || ref.weight === 0 || audit.score === null || audit.score >= 1) {
        continue;
      }

      const details = audit.details as
        { items?: { node?: { selector?: string; snippet?: string } }[] } | undefined;
      const nodes = (details?.items ?? [])
        .map((item) => item.node?.selector ?? item.node?.snippet)
        .filter((s): s is string => Boolean(s))
        .slice(0, 5);
      failing.push(
        `${id}/${audit.id}: ${audit.title}${nodes.length ? ` (${nodes.join(', ')})` : ''}`,
      );
    }
  }

  return failing;
}

const browser = await chromium.launch({ args: [`--remote-debugging-port=${debuggingPort}`] });
const context = await browser.newContext({ baseURL: baseUrl, ignoreHTTPSErrors: true });
const page = await context.newPage();
let remote: PuppeteerBrowser | undefined;
const results: Score[] = [];
mkdirSync(outputDir, { recursive: true });

/** The same tab, as a Puppeteer page for Lighthouse. */
async function puppeteerPage() {
  remote ??= await puppeteer.connect({
    browserURL: `http://127.0.0.1:${debuggingPort}`,
    defaultViewport: null,
    acceptInsecureCerts: true,
  });
  const marker = `lighthouse-${Date.now()}`;
  await page.evaluate((m) => (window.name = m), marker);
  for (const candidate of await remote.pages()) {
    if ((await candidate.evaluate(() => window.name).catch(() => '')) === marker) {
      return candidate;
    }
  }

  throw new Error('The Playwright page was not found over the debugging port.');
}

async function audit(states: State[]): Promise<void> {
  for (const state of states) {
    for (const device of devices) {
      for (const theme of themes) {
        await page.emulateMedia({ colorScheme: theme });
        const flags: Flags = { output: 'html', logLevel: 'error' };
        const config = configFor(device);
        let mode: Score['mode'];
        let lhr: Result | undefined;
        if (typeof state.open === 'string') {
          mode = 'navigation';
          const target = await puppeteerPage();
          lhr = (await lighthouse(new URL(state.open, baseUrl).href, flags, config, target))?.lhr;
          if (lhr && new URL(lhr.finalDisplayedUrl).pathname !== state.open) {
            throw new Error(
              `${state.name}: redirected to ${new URL(lhr.finalDisplayedUrl).pathname}.`,
            );
          }
        } else {
          mode = 'snapshot';
          await state.open(page);
          lhr = (await snapshot(await puppeteerPage(), { config, flags }))?.lhr;
        }

        if (!lhr) {
          throw new Error(`${state.name}: Lighthouse returned no result.`);
        }

        if (lhr.runtimeError) {
          throw new Error(`${state.name}: ${lhr.runtimeError.message}`);
        }

        // Lighthouse runs in its own session; make sure the page was still in the requested theme.
        const dark = await page.evaluate(() => matchMedia('(prefers-color-scheme: dark)').matches);
        if (dark !== (theme === 'dark')) {
          throw new Error(`${state.name}: the ${theme} color scheme was lost during the run.`);
        }

        const file = `${state.name.replace(/[^a-z0-9]+/gi, '-')}-${theme}-${device}`.toLowerCase();
        writeFileSync(join(outputDir, `${file}.html`), generateReport(lhr, 'html'));
        writeFileSync(join(outputDir, `${file}.json`), JSON.stringify(lhr));
        const scores = Object.fromEntries(
          categories.map((id) => [id, Math.round((lhr.categories[id]?.score ?? 0) * 100)]),
        );
        const score: Score = {
          state: state.name,
          theme,
          device,
          mode,
          scores,
          failing: failingAudits(lhr),
        };
        results.push(score);
        console.log(
          `${state.name}, ${theme}, ${device} (${mode}): ${categories.map((id) => `${id} ${scores[id]}`).join(', ')}`,
        );
        for (const line of score.failing) {
          console.log(`  ${line}`);
        }
      }
    }
  }
}

try {
  await audit(signedOut);
  await signIn(page);
  await audit(signedIn);
} finally {
  await remote?.disconnect();
  await browser.close();
}

const below = results.filter((r) => Object.values(r.scores).some((s) => s < 100));
console.log(`\nReports: ${outputDir}`);
if (below.length > 0) {
  console.log(`${below.length} of ${results.length} runs scored below 100.`);
  process.exit(1);
}

console.log(`All ${results.length} runs scored 100.`);
