# Accessibility

ValkyrieID's sign-in and account pages and its admin UI target [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level **AA**. This page describes how they are built and tested, what to keep in mind when you [brand](#branding) the sign-in pages, and how to [run the checks](#running-the-checks) yourself. The results of the last manual review are in the [accessibility checklist](/accessibility/checklist).

## Accessibility statement

- **Scope:** the server-rendered pages (sign-in, two-factor authentication, recovery codes, setup wizard, account pages, password reset, email verification, sign-out, and protocol error pages) and the admin UI under `/admin`.
- **Standard:** WCAG 2.2 levels A and AA.
- **How it's checked:** every pull request to `main` runs [axe](https://github.com/dequelabs/axe-core) on every page and state with both the default and a custom branding, plus checks for reflow, reduced motion, focus visibility, and the strict Content Security Policy. A manual review covers what tools can't check (keyboard use, screen readers, zoom, contrast of branding colors); see the [checklist](/accessibility/checklist).
- **Known limitations:**
  - Custom branding colors, images, and stylesheets are the operator's responsibility: ValkyrieID doesn't check their contrast (see [Branding](#branding)).
  - The QR code for setting up two-factor authentication is an image. The same key is shown as text next to it for users who can't scan it.
  - The audit log viewer and other admin tables scroll horizontally on narrow screens, which WCAG allows for data tables (1.4.10).
- **Feedback:** report accessibility problems as [GitHub issues](https://github.com/ValkyrieID/valkyrieid/issues).

## How the pages are built

The sign-in and account pages work **without JavaScript** and under a strict Content Security Policy (no inline scripts or styles). Accessibility doesn't depend on scripts:

- Every field has a visible `<label>`. Error summaries use `role="alert"` and get the focus when the page loads (`autofocus`); on the sign-in, two-factor, and recovery code forms, the fields are also marked `aria-invalid` and point to the summary with `aria-describedby`. Confirmations use `role="status"`.
- Each page has a unique `<title>`, one `<h1>`, a `<main>` landmark, and `lang="en"`.
- Focus is always visible (a 3 px outline in the primary color), and the order follows the page.
- The layout reflows down to 320 CSS px (400% zoom) without horizontal scrolling, and transitions are turned off with `prefers-reduced-motion`.
- **Accessible authentication (3.3.8):** there are no puzzles or CAPTCHAs. Fields use `autocomplete` (`username`, `current-password`, `new-password`, `one-time-code`), so password managers and authenticator apps can fill them in, and pasting is never blocked, including in the code and recovery code fields.

The admin UI adds the following, since it changes pages without reloading them:

- A **skip link** to the main content comes first.
- After every navigation, the focus moves to the main content and the new page title is announced through a polite live region. The current page is marked with `aria-current="page"` in the navigation.
- Request errors are shown in a summary that gets the focus. Confirmations ("Saved.") are announced through the live region.
- Confirmations of destructive actions are native modal `<dialog>`s: the focus starts on **Cancel**, stays inside the dialog, `Esc` closes it, and the focus returns to the button that opened it.
- Targets are at least 24×24 CSS px, or spaced so that a 24 px circle around each doesn't overlap another (2.5.8).
- The navigation is grouped (Identity, Applications, System); each group is a list named by its visible label, so screen readers announce the group, and every page is still a plain link. On narrow screens every link stays visible, with no menu button to open.
- The admin UI uses the same wallpaper and see-through glass as the account pages, always with the built-in colors and wallpapers (it isn't branded). Measured on the rendered admin pages (top bar, navigation, page content with tables and forms, and dashboard tiles) in light and dark at 1440×900, 1920×1080, and 390 px, at the least favorable point (light / dark): text 12.8:1 / 8.5:1, muted text and table headers 8.0:1 / 5.5:1, links 5.5:1 / 5.5:1, control borders 6.5:1 / 4.3:1, focus outline 4.0:1 / 5.5:1, input text 17.0:1 / 14.5:1. Dialogs are opaque.

## Branding

[Branding](/reference/configuration#branding) changes the sign-in and account pages' colors, logo, background images, and styles. ValkyrieID uses the colors as given, so choose them to keep WCAG 2.2 AA contrast in **both** themes: the colors without `Dark` apply to the light theme, the `Dark*` colors to the dark theme. The default colors meet these ratios (light / dark).

| Variable (setting) | Used for | Must contrast with | Minimum ratio | Default (light / dark) |
|---|---|---|---|---|
| `--vid-text` (`Branding:TextColor`, `Branding:DarkTextColor`) | Text | `--vid-surface` | 4.5:1 | 17.7:1 / 15.7:1 |
| `--vid-primary` (`Branding:PrimaryColor`) | Links, focus outline (light theme) | `--vid-surface` | 4.5:1 (links are text) | 5.5:1 |
| `--vid-link` (derived from `Branding:DarkPrimaryColor`) | Links, focus outline (dark theme): the primary color mixed 40 % with white | `--vid-surface` | 4.5:1 | 10.1:1 |
| `--vid-primary-text` (custom CSS) | Button text, white by default | `--vid-primary` | 4.5:1 | 5.5:1 / 5.5:1 |
| `--vid-muted` (custom CSS) | Hints, "(optional)" | `--vid-surface` | 4.5:1 | 7.7:1 / 7.2:1 |
| `--vid-border` (custom CSS) | Input borders, the selected theme button | `--vid-surface` | 3:1 (1.4.11) | 4.8:1 / 3.8:1 |
| `--vid-error` / `--vid-error-background` (custom CSS) | Error summaries | each other | 4.5:1 | 5.9:1 / 6.4:1 |

- **Primary color:** in the light theme it's both the button background (with white text) and the link and focus color on the card, so it needs 4.5:1 against **both** white and the surface color. Very light or very saturated colors (yellow, light green, orange) usually fail; darken them. If you need a light primary color, set `--vid-primary-text` to a dark color in the custom stylesheet. In the dark theme, buttons use the dark primary color with white text, and links use a lighter tint of it, so a mid-tone color works best.
- **Glass surfaces:** text sits on see-through frosted glass over the wallpaper. Because the backdrop varies, muted text, control borders, and links on glass are moved toward the text color (`--vid-glass-muted`, `--vid-glass-control-border`, `--vid-glass-link`, derived from your colors), and inputs get a mostly opaque fill. Measured on the rendered sign-in and account pages with the built-in wallpapers, at the least favorable point (light / dark): text 11.4:1 / 8.4:1, muted text 7.1:1 / 5.4:1, links 4.8:1 / 5.4:1, control borders 5.7:1 / 4.2:1, focus outline 3.5:1. Where `backdrop-filter` isn't supported, the glass is the plain surface color.
- **Background images:** they fill the page behind the glass surfaces and are decorative (a CSS background, invisible to screen readers and hidden in forced-colors mode), so they mustn't carry information. Glass is see-through, so a custom image changes what's behind the text: whenever a branding image applies to a theme, that theme's glass becomes mostly opaque (88 % of the surface color), which keeps the ratios of the table above for any image. The logo and product name on the sign-in pages sit directly on the image; with the built-in wallpapers, "Valkyrie" reaches 6.2:1 and the large, muted "id" 3.3:1 (large text needs 3:1).
- **Logo:** the logo's `alt` text is the product name (`Branding:ProductName`), so don't include text in the logo that differs from it.
- **Custom stylesheets** can break conformance: don't hide focus outlines (`outline: none`), don't fix heights or widths in a way that clips text at 200% zoom or 320 px, keep controls at least 24×24 px, and wrap animations in `@media (prefers-reduced-motion: no-preference)`.
- Check your branding with a contrast checker (e.g. the browser developer tools' contrast ratio), or run the [checks](#running-the-checks) with your own colors in `tests/ValkyrieID.A11yTests/tests/branded.spec.ts`.

## Running the checks

The automated checks are a [Playwright](https://playwright.dev) project in `tests/ValkyrieID.A11yTests`, using [`@axe-core/playwright`](https://github.com/dequelabs/axe-core-npm). They need the .NET SDK and Node.js.

```sh
cd tests/ValkyrieID.A11yTests
npm ci
npx playwright install chromium   # once; on Linux add --with-deps
npm test
```

`npm test`:

1. publishes the server with the admin UI to `.run/server` (set `A11Y_SKIP_PUBLISH=1` to reuse the last build);
2. starts it on `https://localhost:5199` with the .NET development certificate, a new SQLite database, and an SMTP sink that writes emails to `.run/mail`;
3. runs the setup wizard (creating the admin with 2FA) and creates users, a client, and an API resource with the [CLI](/reference/cli);
4. checks every sign-in, account, and protocol page and every admin UI route, including dialogs, empty states, and error states, first with the default branding, then with a custom one (`fixtures/branding`).

A check fails on any axe violation tagged WCAG 2.0/2.1/2.2 A or AA, on horizontal scrolling at 320 CSS px, on animations with reduced motion, on a missing or hidden focus indicator in the keyboard tests, and on any Content Security Policy violation. `npm run report` opens the HTML report of the last run. Ports can be changed with `A11Y_PORT` and `A11Y_SMTP_PORT`.

To run the checks on Linux like CI, use the Playwright image (it has Node.js and the browsers; the .NET SDK is installed in the container). The repository is copied into the container, so your local `node_modules`, `bin`, and `obj` folders aren't touched:

```sh
docker run --rm -v "$PWD:/src:ro" mcr.microsoft.com/playwright:v1.65.0-noble bash -c '
  set -e; mkdir /work; cd /src
  tar --exclude=node_modules --exclude=bin --exclude=obj --exclude=.run -cf - . | tar -xf - -C /work
  cd /work; curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --jsonfile global.json --install-dir /usr/share/dotnet
  export PATH=/usr/share/dotnet:$PATH; cd tests/ValkyrieID.A11yTests; npm ci; CI=true npm test'
```

Keep the image tag in step with the `@playwright/test` version in `package.json`.

In CI, the checks run on pull requests to `main` and on manual runs (not on every push), in one Linux job with Chromium. When they fail, the job uploads the HTML report and traces as the `accessibility-report` artifact.
