import { cli } from '../support/env.ts';
import { expect, test } from '../support/a11y.ts';
import { defineLoginPageTests } from '../support/login-suite.ts';

/** The custom branding fixture (fixtures/branding): logo, background image, colors and a custom stylesheet. */
const branding: Record<string, string> = {
  'Branding:ProductName': 'Example Corp',
  'Branding:Logo': 'logo.svg',
  'Branding:BackgroundImage': 'background.svg',
  'Branding:CustomCss': 'custom.css',
  'Branding:PrimaryColor': '#0f6b5c',
  'Branding:BackgroundColor': '#1d2433',
  'Branding:SurfaceColor': '#fdfcf7',
  'Branding:TextColor': '#22262e',
};

test.describe('login pages, custom branding', () => {
  test.beforeAll(async ({ browser }) => {
    for (const [key, value] of Object.entries(branding)) {
      await cli(['settings', 'set', key, value]);
    }

    // Settings:ReloadInterval is 1 s in the test server.
    const page = await browser.newPage();
    await expect
      .poll(
        async () => {
          await page.goto('/account/login');
          return page.title();
        },
        { timeout: 15_000 },
      )
      .toContain('Example Corp');
    await page.close();
  });

  defineLoginPageTests('branded');
});
