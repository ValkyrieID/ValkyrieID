# ValkyrieID UI layout and theme plan

This plan covers the visual refresh of the server-rendered Razor pages and the Angular admin UI. It is tracked as roadmap phase 11 in `PROJECT.md`, before the deferred protocol additions.

## Goals

- Give the login, account, setup, and admin experiences one coherent ValkyrieID visual language.
- Support light, dark, and system themes.
- Improve hierarchy, spacing, navigation, dense data presentation, and responsive behavior without weakening accessibility or the existing security boundaries.
- Preserve the current WCAG 2.2 AA goals, semantic markup, keyboard support, skip navigation, route-focus management, and live announcements.

## Current observations

- The Angular admin shell is functional but visually flat, and the navy, purple, and orange look feels dated. Phase 11 replaces it with a new visual identity (below).
- The dashboard metric cards are useful, while the recent-activity table dominates the page and wraps UUIDs and details character-by-character.
- Admin list pages have a consistent basic structure, but create forms, empty states, badges, and explanatory content need stronger visual patterns.
- The audit log needs a compact default view with expandable details rather than displaying all long identifiers and structured data inline.
- The server-rendered login and account pages use a clean but generic centered card. The account landing page is currently a sparse link list.
- `/setup` is currently unavailable because the first administrator already exists. Its source uses the shared Razor layout, so the refresh must still cover the first-run state.
- Lighthouse currently reports 100 accessibility and 100 best practices for the desktop admin dashboard. The refresh must preserve or improve that result.

## Shared design foundation

Phase 11 introduces a new visual identity: **calm surfaces with a single teal accent and a touch of gold, taken from the logo**. Professional, calm, and quietly polished; color is used for meaning (actions, links, focus, status), and gold only for decoration.

Principles:

- neutral gray surfaces; teal only for primary actions, links, focus, and the active navigation item; gold only for decorative highlights (the logo glow and the card's top edge), never for text or controls
- crisp 1 px dividers and subtle surface steps instead of heavy shadows; in the dark theme, raised surfaces get lighter instead of casting shadows
- small radii (6 px for controls, 8 px for cards), a 4/8 px spacing grid, the system font stack, and a monospace stack for identifiers and keys
- a compact type scale with clear weight steps; muted text for secondary information
- every control (input, select, button) shares one height, radius, and border; primary, secondary, subtle, and danger buttons have distinct roles, and hover, active, disabled, and focus states are visible in both themes
- the admin UI uses the same wallpaper and glass as the account pages: a glass top bar, a grouped glass sidebar whose active item gets the teal tint and a marker, and the page on a glass panel (11c, 11d)
- dark theme on neutral charcoal surfaces (not pure black, not navy)

Shared tokens (CSS custom properties, `--vid-*`, defined in both `site.css` and the Angular `styles.css`):

| Token | Light | Dark | Use |
|---|---|---|---|
| `--vid-background` | `#fafafa` | `#0a0a0b` | Page |
| `--vid-surface` | `#ffffff` | `#141416` | Cards, sidebar, inputs |
| `--vid-surface-raised` | `#ffffff` | `#1c1c1f` | Menus, dialogs |
| `--vid-surface-hover` | `#f4f4f5` | `#232327` | Hover, table headers, subtle fills |
| `--vid-text` | `#18181b` | `#ededef` | Text |
| `--vid-muted` | `#52525b` | `#a1a1aa` | Secondary text |
| `--vid-divider` | `#e4e4e7` | `#27272a` | Dividers, card borders |
| `--vid-border` | `#71717a` | `#71717a` | Control borders (3:1) |
| `--vid-primary` | `#0f766e` | `#0f766e` | Primary buttons (white text, 5.5:1) |
| `--vid-primary-hover` | primary mixed with black | primary mixed with black | Primary button hover |
| `--vid-link` | `--vid-primary` | primary mixed with white | Links and accent text |
| `--vid-focus` | `--vid-primary` | primary mixed with white | Focus rings |
| `--vid-accent-tint` | 10 % primary | 22 % primary | Active navigation, selection |
| `--vid-accent` | `#b8862f` | `#d4a84b` | Gold, decorative only (logo glow, card edge); never text or controls |
| `--vid-background-image` | `img/wall-light.png` | `img/wall-dark.png` | Page wallpaper (Razor pages and admin UI); on the Razor pages overridden by `Branding:BackgroundImage` / `Branding:DarkBackgroundImage` |
| `--vid-glass` | 50 % surface | 45 % slate-teal (surface mixed with `#6b8a8c`) | See-through frosted glass on the wallpaper (Razor pages and admin UI), with a 12 px backdrop blur; plain surface without `backdrop-filter` |
| `--vid-glass-border` | 60 % white | 12 % white | Glass surface edge |
| `--vid-glass-muted`, `--vid-glass-control-border`, `--vid-glass-link` | muted, border, and primary moved toward the text color | same, link unchanged | Replace `--vid-muted`, `--vid-border`, and `--vid-link` on glass, so they keep 4.5:1 and 3:1 over the wallpaper |
| `--vid-glass-input` | 85 % surface | 80 % surface | Input fill on glass (mostly opaque) |
| `--vid-wordmark`, `--vid-wordmark-muted` | `#1f2937`, `#4b5059` | `#e8eaed`, `#a8adb5` | "Valkyrie" and the large lockup's "id" (3:1, large text only; the small wordmark's "id" uses `--vid-muted`) |
| `--vid-error`, `--vid-ok`, `--vid-warn`, `--vid-info` | `#b91c1c`, `#15803d`, `#a16207`, `#1d4ed8` | `#f87171`, `#4ade80`, `#facc15`, `#93c5fd` | Status text, with matching `-background` tints |

Derived tokens (`--vid-link`, `--vid-focus`, `--vid-primary-hover`, `--vid-accent-tint`, `--vid-glass`) are computed with `color-mix()` from `--vid-primary` and `--vid-surface`, so branding colors carry through. Every text pair must reach 4.5:1 and every control, border, and focus pair 3:1 in both themes, including on glass over the brightest (dark theme) or darkest (light theme) part of the wallpaper.

The logo (a teal winged shield with a gold edge) and both wallpapers live in `assets/branding`; the served copies are in `src/ValkyrieID.Server/wwwroot/img` and the admin UI's `public` folder.

The server-rendered and Angular styles use the same token names and values, including the glass tokens and rules; keep `site.css` and `styles.css` in sync. Branding overrides keep working on the Razor pages: the branding colors apply to the light theme, and optional `Branding:Dark*` colors apply to the dark theme. Operators remain responsible for the contrast of custom colors. The admin UI is not branded: it doesn't load `/theme.css` and always uses the built-in colors and wallpapers.

## Theme behavior

Support three modes:

1. **System** — follow `prefers-color-scheme`.
2. **Light** — explicitly use the light palette.
3. **Dark** — explicitly use the dark palette.

New users default to System. The selected mode is applied through a shared `data-theme` attribute (`system`, `light`, or `dark`) on `<html>`:

- **Preference:** the `__Host-valkyrie.theme` cookie (`light` or `dark`; System deletes it). `Secure`, `Path=/`, `SameSite=Lax`, one year, not `HttpOnly` because the admin UI reads and writes it. It is a display preference only and is never trusted for anything else; unknown values count as System.
- **Razor pages:** the server renders `data-theme` from the cookie, so the right theme shows on first paint without any script, and the strict CSP is unchanged. The theme control is a small form at the top right, first in the page order (WCAG 1.3.2, 2.4.3): a labelled group of three buttons (System, Light, Dark) with `aria-pressed` on the current one, posting to `POST /account/theme` (anti-forgery token, rate-limited, local return URL only, 303 back to the page).
- **Admin UI:** `index.html` starts with `data-theme="dark"`; at startup the app reads the cookie and switches to the stored mode before the first render. A dark-to-light switch is acceptable, a light-to-dark flash is not. The same three-button control sits in the top bar, holds its state in a signal, and applies changes immediately.
- `prefers-color-scheme` is the fallback everywhere, so the no-JavaScript default stays usable.

Theme controls have an accessible name and state, meet the 24 px target size, and focus and contrast are verified in every theme. Forced-colors mode falls back to system colors, and reduced motion disables transitions.

## Phase 11a: Shared visual foundation and themes

- Define the shared design tokens and light/dark palettes.
- Establish typography, spacing, surface, border, shadow, radius, and status patterns.
- Add theme preference handling and the theme control.
- Apply theme variables to both `site.css` and the Angular global styles.
- Verify contrast, focus indicators, reduced motion, and forced-colors behavior where practical.

## Phase 11b: Razor authentication and account experience

Affected areas include the shared Razor layout, login, logout, error, 2FA, recovery-code, password-reset, email-verification, account, profile, password, two-factor, sessions, and setup pages.

Every page sits on a full-page wallpaper, with content on frosted-glass surfaces. Two layouts replace the single centered card. Everything stays server-rendered, works without JavaScript, and keeps the strict CSP (`style-src 'self'`, no script).

### Wallpaper and glass

- **Wallpaper:** fills the page behind every Razor page (cover, centered, fixed). The light theme shows `Branding:BackgroundImage`, or the built-in `wall-light.png`; the dark theme shows `Branding:DarkBackgroundImage`, then `Branding:BackgroundImage`, then the built-in `wall-dark.png`. It is decorative (a CSS background, no markup, no text in the image) and hidden in forced-colors mode.
- **Glass:** the sign-in card, the account top bar, navigation, and content, and the theme switch are see-through frosted glass (`--vid-glass` with `backdrop-filter: blur()` and a thin `--vid-glass-border`); the wallpaper's lines and nodes show softly through it. The sign-in card adds a faint teal glow in its top-left corner. On glass, muted text, control borders and links use the `--vid-glass-*` variants and inputs a mostly opaque fill, so text keeps 4.5:1 and controls 3:1 over any part of the wallpaper (measured on the rendered pages). Without `backdrop-filter` support, the glass is the plain surface. The Angular admin UI adopts the same wallpaper and glass in phases 11c and 11d. The see-through glass is tuned and measured for the built-in wallpapers; when a branding background image applies to a theme, `/theme.css` makes that theme's glass mostly opaque (88 % surface), so text keeps its contrast over any image.

### Sign-in layout (lockup and glass card)

Used by sign-in, 2FA, recovery code, 2FA enrollment at sign-in, forgot/reset password, email verification and confirmation, sign-out, setup, and the protocol error pages (`ProtocolPages`). The protocol pages have no theme switch: its form would replay the protocol request, and the `form_post` page's `form-action` only allows the client. They still render the stored theme.

- **Lockup:** the logo, with a soft teal and gold glow, above the product name. With the built-in name, the product name is the "Valkyrie id" wordmark: bold "Valkyrie" and a lighter "id", hidden from assistive technology, which reads the visually hidden product name "ValkyrieID" instead. A custom `Branding:ProductName` or `Branding:Logo` replaces it.
- **Desktop (≥ 64rem):** the lockup on the left, the glass card with the form on the right (about 28rem), both vertically centered; the theme switch is at the top right of the page and first in the focus order. A thin teal-to-gold line marks the card's top edge.
- **Mobile:** the theme switch, then the smaller lockup above the glass card, which fills the width with a small margin.
- Improve hierarchy, field spacing, action areas, and error/success messages (status tokens with an icon plus text, never color alone). Keep password and 2FA warnings prominent without exposing sensitive data.

### Account layout (app style)

Used by `/account` and its sub-pages (profile, email, password, two-factor, sessions).

- A glass top bar with the logo and product name, the signed-in user, the theme switch, and Sign out.
- Account navigation (Profile, Email, Password, Two-factor, Sessions) on a glass panel with `aria-current="page"`, and a skip link before it.
- The page content sits on one glass surface. Inside it, the account home becomes grouped, divided link lists (title, description, decorative chevron) with clear focus/hover states, and sections and lists are separated by dividers rather than boxed again.
- Every page has the same structure: back link, title, short explanation, settings, action area above a divider; destructive actions are in a separate section with a red heading.
- Sessions are a divided list with compact metadata; the current session has a badge and an accent bar.

### Motion (CSS only)

- **No JavaScript and no success interstitial:** after a successful sign-in the server redirects straight to the application.
- **Entry animation:** the wallpaper sharpens in (fade from a light blur), the lockup rises in, and the card rises and scales in with its content staggered; about 0.7 s total. It plays only when the server adds a class to `<html>`: on a fresh GET of a flow's entry page (sign-in, setup, forgot password). Never on error re-renders after a POST or on later steps such as 2FA.
- **Step transitions:** cross-document view transitions (`@view-transition { navigation: auto; }`) between our own pages, with the lockup and the card given their own `view-transition-name` so they stay put while the form changes. Browsers without support just navigate.
- The account pages have no entry animation, only view transitions.
- `prefers-reduced-motion: reduce` turns off all animations and view transitions.

### Acceptance

- Integration tests: the entry-animation class only on fresh GETs of entry pages (not after a POST, not on 2FA); both layouts render the lockup and glass surfaces; the built-in wallpapers are served and branding images override them per theme; the account navigation marks the current page.
- The accessibility suite passes on every page in light and dark, at 320 px, and with reduced motion.
- Desktop and mobile screenshots of sign-in, 2FA, setup, account, and sessions in both themes.
- The docs explain that the branding background images fill the page behind the glass surfaces, and that text outside them (the lockup) needs contrast with the image.

## Phase 11c: Angular admin shell and navigation

The admin shell matches the account layout (Phase 11b), with the same tokens, glass rules, and measurements. Done.

- **Wallpaper and glass:** the built-in light and dark wallpapers (`public/img`) fill the page; the top bar, sidebar, page content, and theme switch are the same see-through glass as on the account pages, with the `--vid-glass-*` variants on glass and the plain surface without `backdrop-filter`. The admin UI is not branded (no `/theme.css`), so the glass is always the measured see-through glass.
- **Top bar:** the logo and the "Valkyrie id" wordmark (visual only; assistive technology reads "ValkyrieID") with an "Admin" label, the theme switch, the signed-in user, and Sign out.
- **Navigation:** a glass panel with Dashboard and three labelled groups (Identity: users, roles, groups; Applications: clients, API resources; System: signing keys, audit log, settings). Each group is a list named by its label (`aria-labelledby`), so screen readers announce the groups; every route stays a plain link. The current page has `aria-current="page"`, the teal tint, weight, and a marker.
- **Responsive:** from 48rem the navigation is a sticky sidebar beside the content; below, each group is one row (label beside its links) above the content, so every route stays visible without a menu button.
- **Signed-out, sign-in error, and signing-in states:** the logo and title on a glass card on the wallpaper, like the sign-in card.
- The skip link, route focus management, live announcements, and the dark-first theme startup are unchanged.

## Phase 11d: Angular page content and data presentation

Done.

### Done: glass look

- Page content sits on one glass panel; sections, lists, settings sections, and table rows are separated by dividers, not boxed again. Headers, forms, inputs (mostly opaque `--vid-glass-input`), buttons, badges, alerts, and notices use the same patterns as the account pages; alerts and notices have an icon as well as color.
- Dashboard tiles are a more opaque glass (`--vid-glass-input`).
- Dialogs and menus stay opaque (`--vid-surface-raised`) for readability over the page.
- Tables sit directly on the glass. Measured on the rendered pages (content hidden through the CSSOM, worst backdrop pixel per surface, light and dark at 1440×900, 1920×1080, and 390 px), every text pair keeps 4.5:1 and every control 3:1, so table bodies need no opaque fill. If a later change lowers the measured ratios, give the table body an opaque fill instead of weakening the tokens.

### Done: page content and data presentation

- **Page structure:** every page uses one header component (`app-page-header`): an optional back link, the `<h1>` with status badges, a muted lead line, and the primary action on the right. Errors (`app-problem`) follow directly below. Create pages end with the submit button and **Cancel** below a divider (`.form .actions`). Detail pages put disabling and deleting in a **Danger zone** at the end: a ⚠-marked red heading (on glass the red moves toward the text color: 4.92:1 measured) and one row per action with its consequence. Enable stays a normal action in the header.
- **Loading and empty states:** `app-loading` (a `role="status"` "Loading…" with a decorative spinner, still with reduced motion) and `app-empty` (icon, message, optional hint, and the next step: **Clear search**, or **Create the first …**).
- **Status badges:** text plus a mark, never color alone: ✓ ok/active (green), ! warn (yellow), – off/retired (neutral, dashed border), ○ pending (blue), ◆ built in (teal), ⊘ locked by configuration. Danger buttons carry ⚠.
- **Dashboard:** metric tiles with an icon, the value, the label, and a detail line (signing key algorithm, activation date, pending rotation). Recent activity is a compact divided list (result badge, event, time, shortened actor) instead of the audit table.
- **Lists:** one "main" column per row (link plus a muted second line, e.g. display name and email) instead of many narrow columns; grant types and scopes as code chips; status as badges. Roles and groups have a **New** header action that moves the focus to the form on the page. Signing keys are sorted active, pending, retired, with a legend of the states. Settings sections are a link list with chevrons, like the account overview.
- **Audit log:** compact rows (time, event, result, shortened actor). A disclosure button (`aria-expanded`, `aria-controls`, named "Details of _event_, _time_") opens a full-width row below with the full time, entry ID, actor, subject (with a link to the user), client, IP address, and details as aligned label/value pairs. Expanded rows get the teal tint (and an outline in forced colors).
- **IDs:** `app-id` shows UUIDs as their first 8 characters (`admin:01a1c0fd…`) and other long values as their first 12, with the full value as visually hidden text and a copy button (Clipboard API only; hidden without it; announces "… copied."). Detail views show the value in full.
- **Small screens:** below 48rem, tables marked `appStack` (`StackTableDirective`) become one card per row with the column name beside each value (`data-label`, generated content with empty alternative text). The directive sets explicit ARIA table roles, so the semantics survive `display: block`; the header row stays available to assistive technology.
- **Measured** on the rendered pages (text hidden through the CSSOM, worst backdrop pixel under each text node and icon), light and dark at 1440×900, 1920×1080, 390 px, and 320 px: lowest text pair 4.76:1 (badges), icons 7.32:1, no horizontal overflow. No new surface needed an opaque fill.

## Phase 11e: Responsive polish and verification

- Review all admin, login, account, and setup states at desktop and mobile sizes.
- Verify light, dark, and system themes on every shared component.
- Run Lighthouse and axe checks against representative pages and states.
- Perform keyboard-only and screen-reader review, including dialogs, expandable audit details, theme controls, and destructive actions.
- Add regression tests for theme preference behavior, responsive table rendering, account navigation, and audit-log details.
- Update the accessibility checklist and relevant documentation.

## Suggested implementation order

1. Shared tokens and theme foundation.
2. Razor shell, login, account, and setup styling.
3. Angular shell and responsive navigation.
4. Shared Angular controls and state patterns.
5. Dashboard and data-page redesign.
6. Audit-log redesign.
7. Mobile refinement and accessibility verification.

## Acceptance criteria

- Light, dark, and system theme modes work consistently across Razor and Angular pages.
- Login, account, setup, and admin pages share the same visual language while retaining their appropriate security messaging.
- The admin dashboard and list pages are visually clearer at desktop and mobile widths.
- Audit entries no longer require character-by-character wrapping of UUIDs and details for normal scanning.
- Keyboard navigation, focus visibility, semantic labels, skip navigation, route announcements, and reduced-motion behavior remain intact.
- Automated accessibility checks pass for representative light and dark states, with no regression from the current WCAG 2.2 AA baseline.
