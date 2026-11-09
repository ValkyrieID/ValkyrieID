# Accessibility checklist

This checklist records how ValkyrieID meets [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level AA, beyond what the [automated checks](/accessibility/#running-the-checks) find. It was written during the phase 10c accessibility pass (November 2026) and is kept up to date with the pages.

- **Login pages** are the server-rendered pages: sign-in, 2FA, recovery code, setup wizard and 2FA enrollment, the account pages (profile, password, 2FA, sessions, email), forgot and reset password, email verification and change, sign-out, and the protocol error and `form_post` pages.
- **Admin UI** is the Angular app under `/admin`, including its dialogs and its empty, error, signed-out, and not-authorized states.
- **Method:** *axe* means the axe rules in `tests/ValkyrieID.A11yTests`, which run on every page and state with both brandings; *test* names a specific check in that project (`support/a11y.ts`, `support/login-suite.ts`, `tests/admin-ui.spec.ts`), an integration test (`tests/ValkyrieID.Server.IntegrationTests`), or an admin UI unit test; *manual* is a review in Chrome with the keyboard, the developer tools, and zoom.
- **Status:** ✅ meets the criterion · 🔁 re-check with a screen reader before each release (see [Screen reader pass](#screen-reader-pass)) · ➖ not applicable.

## Keyboard

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| Everything works with the keyboard | 2.1.1 | Plain links, buttons, and form fields; no custom widgets | Native buttons, links, form fields, and `<dialog>`; no click-only elements | manual; Angular template accessibility lint rules | ✅ |
| No keyboard traps | 2.1.2 | No scripts that capture keys | Modal dialogs keep the focus inside while open and close with `Esc`; nothing else captures the focus | test: `checkDialogs` (opens every confirmation dialog, closes it with `Esc`) | ✅ |
| Focus order follows the page | 2.4.3 | DOM order; the first field has `autofocus`, or the error summary after a failed submission | Skip link, header, navigation, main content. Focus moves to the main content after navigation and returns to the opening button when a dialog closes | test: `tabThrough` on sign-in; `dashboard and navigation`; `checkDialogs` | ✅ |
| Focus is visible | 2.4.7 | 3 px outline in the primary color with a 2 px offset on fields, buttons, links, and the error summary | 3 px outline (`:focus-visible`), orange on the dark header and navigation | test: `tabThrough` checks for an outline of at least 2 px on every focused element | ✅ |
| Focus isn't hidden | 2.4.11 | No sticky or fixed elements | No sticky header or banners; dialogs are modal and centered | test: `tabThrough` checks the focused element is on top at its center | ✅ |
| Bypass blocks (skip link) | 2.4.1 | ➖ No repeated navigation: the only content before the form is the logo or product name, inside `<main>` | "Skip to content" is the first focusable element and moves the focus to `<main>` | test: `dashboard and navigation` | ✅ |
| Shortcuts | 2.1.4 | ➖ none | ➖ none | manual | ➖ |

## Screen readers

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| Page titles | 2.4.2 | "Page – Product name", unique per page | "Page – ValkyrieID Admin" from the route title, announced after navigation | axe (`document-title`); test: `toHaveTitle` | ✅ |
| Headings | 1.3.1, 2.4.6 | One `<h1>` per page and state; `<h2>` for sections (recovery codes, 2FA options, email change) | One `<h1>` per route, including error states of detail pages (fixed in 10c, see below); `<h2>` per section | axe; manual | ✅ |
| Landmarks | 1.3.1 | `<main>` | Header (`banner`), navigation "Main", `<main>`; search forms have `role="search"` | axe; manual | ✅ |
| Labels | 1.3.1, 3.3.2, 4.1.2 | Every field has a visible `<label>`; hints are linked with `aria-describedby` | Visible labels, `<fieldset>`/`<legend>` for groups; hints linked with `aria-describedby` | axe (`label`); admin UI unit tests | ✅ |
| Button and link names | 2.4.4, 2.4.6, 4.1.2 | Repeated "Sign out this session" buttons are described by their device (`aria-describedby`, added in 10c) | Text buttons; dialogs are labelled by their heading | axe; integration test: `Sessions_page_lists_only_the_users_own_active_sessions` | ✅ |
| Error messages | 3.3.1, 4.1.3 | Error summary with `role="alert"`, focused on load without JavaScript (`autofocus`, added in 10c); sign-in, 2FA, and recovery code fields are `aria-invalid` and point to it | Error summary with `role="alert"` that takes the focus; fields with errors are `aria-invalid` | integration test: `Sign_in_errors_get_the_focus_and_are_linked_to_the_fields`; test: `new user with errors` (focus) | ✅ |
| Status messages | 4.1.3 | Confirmations ("Your profile has been saved.") use `role="status"` | Confirmations and page changes are announced through a polite live region; loading states use `role="status"` | test: `role=status` after each form; manual | ✅ |
| Images | 1.1.1 | The default logo is decorative next to the product name; a custom logo's `alt` is the product name; the QR code has an `alt`, and its key is shown as text | Logo images are decorative next to the product name | axe (`image-alt`) | ✅ |
| Language | 3.1.1 | `lang="en"` | `lang="en"` | axe (`html-has-lang`) | ✅ |

### Screen reader pass

Tools can't tell whether announcements make sense. Before each release, go through these flows with NVDA and Chrome on Windows (or VoiceOver and Safari on macOS) and update the date:

1. Setup wizard with a mismatched password, then 2FA enrollment, then the recovery codes.
2. Sign-in with a wrong password, then 2FA with a wrong code, then a recovery code.
3. Account pages: save the profile, change the password, turn 2FA on and off, sign out a session.
4. Admin UI: navigate between pages (title announced), create a user with errors, open and cancel a delete dialog, save a setting.

| Flow | Last checked | Notes |
|---|---|---|
| 1–4 | 🔁 not yet (first release) | Structure, names, and live regions were checked in the Chrome accessibility tree during 10c. |

## Zoom and reflow

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| 200% zoom, text resize | 1.4.4 | Relative units (`rem`); no fixed heights, nothing clipped | Relative units; the navigation wraps below 48 rem | manual (Chrome zoom, 640×400 CSS px) | ✅ |
| 400% zoom (320 CSS px) without horizontal scrolling | 1.4.10 | Single column card | Single column below 48 rem; data tables scroll inside their own container (allowed for tables) | test: `checkPage` checks every page at 320 px. Fixed in 10c: the layout grid grew with wide tables, and visually hidden table headers escaped the table's scroll container | ✅ |
| Text spacing | 1.4.12 | No fixed heights on text containers | No fixed heights on text containers | manual (text spacing bookmarklet) | ✅ |

## Contrast

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| Text contrast | 1.4.3 | Light / dark theme: 17.7 / 15.7:1 (text), 7.7 / 7.2:1 (hints), 6.3 / 9.6:1 (links), 6.3:1 (buttons), 5.9 / 6.4:1 (errors) | Same palette in both themes: text, links, badges, and alerts at least 4.5:1 | axe (`color-contrast`) on every page | ✅ |
| Non-text contrast (borders, focus) | 1.4.11 | Light / dark: input borders and the selected theme button 4.8 / 3.8:1, focus outline 6.3 / 9.6:1 | Same palette: input borders, the selected theme button, and focus outlines at least 3:1 | manual (DevTools contrast) | ✅ |
| Branding colors | 1.4.3, 1.4.11 | Operator's choice: [requirements](/accessibility/#branding). The custom branding fixture passes all checks | ➖ the admin UI isn't branded | axe with `fixtures/branding` | ✅ (fixture) |
| Use of color | 1.4.1 | Errors are text, not only a red border | States are text badges ("disabled", "locked out") | manual | ✅ |

## Motion and timing

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| Reduced motion | 2.3.3 | No animations; transitions off with `prefers-reduced-motion` | Transitions and animations off with `prefers-reduced-motion` | test: `checkPage` checks no element animates with reduced motion | ✅ |
| Timing | 2.2.1 | Two-factor step: 5 minutes after the password; email links 24 h. A timeout only means starting the step again, nothing is lost | An expired session shows a message and a "Sign in again" button; unsaved changes on that page are lost (stated in the message) | manual | ✅ |
| Nothing flashes or moves on its own | 2.2.2, 2.3.1 | ➖ | ➖ | manual | ➖ |

## Input

| Check | WCAG | Login pages | Admin UI | Method | Status |
|---|---|---|---|---|---|
| Target size | 2.5.8 | Buttons span the card; links are inline text or 24 px high lines | Buttons at least 40 px high; link buttons 24 px; checkboxes 20 px with spacing | axe (`target-size`) | ✅ |
| Accessible authentication | 3.3.8 | No cognitive tests or CAPTCHAs. `autocomplete` on every credential field (`username`, `current-password`, `new-password`, `one-time-code`), so password managers and authenticator apps can fill them; pasting is never blocked, including the TOTP and recovery code fields. The TOTP key can be copied as text | Signs in through the login pages | integration test: `Sign_in_fields_work_with_password_managers`; test: 2FA field attributes; manual (paste) | ✅ |
| Redundant entry | 3.3.7 | Values are kept after a failed submission (except passwords); the username is pre-filled when known | Forms keep their values after errors | manual | ✅ |
| Error prevention | 3.3.4 | Changing the password, email, or 2FA asks for the current password | Deleting and disabling ask for confirmation | manual | ✅ |
| Consistent help and navigation | 3.2.3, 3.2.6 | Same card layout; "Back to your account" in the same place | Same header and navigation on every page | manual | ✅ |
| No unexpected changes | 3.2.1, 3.2.2 | Forms submit only with their button; without JavaScript, the `form_post` page shows a **Continue** button | Fixed in 10c: the sign-in error and signed-out pages were immediately replaced by a new sign-in | admin UI unit test: `freshTokenGuard` | ✅ |
| Works without JavaScript and under the strict CSP | — | Every flow, including `form_post` | ➖ the admin UI is a single-page app | test: `protocol pages` (no JavaScript); every test fails on a CSP violation | ✅ |

## Fixed in the 10c pass

| Problem | WCAG | Fix | Regression test |
|---|---|---|---|
| Admin UI pages scrolled horizontally at 320 px: the single-column layout grew with wide tables | 1.4.10 | `grid-template-columns: minmax(0, 1fr)` (`styles/shell.css`) | `checkPage` (every admin page) |
| Visually hidden table headers (absolutely positioned) escaped the tables' scroll containers and widened the page | 1.4.10 | `.table-wrap { position: relative }` | `checkPage` (API resource with a new secret) |
| Detail pages of a missing user, client, resource, role, or group were blank: reading a failed resource's value threw, so neither a heading nor the error showed | 1.3.1, 2.4.6, 3.3.1 | `valueOf()` helper; an `<h1>` in the error state | `UserDetailPage` unit test; `unknown user`, `unknown client` checks |
| The admin UI's sign-in error and signed-out pages were replaced by a new sign-in at once, so they couldn't be read or acted on | 3.2.1, 3.3.1 | The token renewal guard only runs while signed in | `freshTokenGuard` unit tests; `sign-in error`, `signed out` checks |
| The error summary on login pages wasn't focused without JavaScript, so screen readers didn't read it first | 3.3.1, 4.1.3 | `autofocus` on the summary (`_Errors.cshtml`, invalid link messages) | `Sign_in_errors_get_the_focus_and_are_linked_to_the_fields` |
| "Sign out this session" buttons had the same name with nothing to tell them apart | 2.4.6 | `aria-describedby` pointing to the session's device | `Sessions_page_lists_only_the_users_own_active_sessions` |
