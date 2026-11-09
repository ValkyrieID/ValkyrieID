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
- the admin sidebar uses the page surface with a thin divider (no colored bar), implemented in 11c
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
| `--vid-background-image` | `img/wall-light.png` | `img/wall-dark.png` | Razor page wallpaper; overridden by `Branding:BackgroundImage` / `Branding:DarkBackgroundImage` |
| `--vid-glass` | 50 % surface | 45 % slate-teal (surface mixed with `#6b8a8c`) | See-through frosted glass on the wallpaper (Razor pages), with a 12 px backdrop blur; plain surface without `backdrop-filter` |
| `--vid-glass-border` | 60 % white | 12 % white | Glass surface edge |
| `--vid-glass-muted`, `--vid-glass-control-border`, `--vid-glass-link` | muted, border, and primary moved toward the text color | same, link unchanged | Replace `--vid-muted`, `--vid-border`, and `--vid-link` on glass, so they keep 4.5:1 and 3:1 over the wallpaper |
| `--vid-glass-input` | 85 % surface | 80 % surface | Input fill on glass (mostly opaque) |
| `--vid-wordmark`, `--vid-wordmark-muted` | `#1f2937`, `#4b5059` | `#e8eaed`, `#a8adb5` | "Valkyrie" and the large lockup's "id" (3:1, large text only; the small wordmark's "id" uses `--vid-muted`) |
| `--vid-error`, `--vid-ok`, `--vid-warn`, `--vid-info` | `#b91c1c`, `#15803d`, `#a16207`, `#1d4ed8` | `#f87171`, `#4ade80`, `#facc15`, `#93c5fd` | Status text, with matching `-background` tints |

Derived tokens (`--vid-link`, `--vid-focus`, `--vid-primary-hover`, `--vid-accent-tint`, `--vid-glass`) are computed with `color-mix()` from `--vid-primary` and `--vid-surface`, so branding colors carry through. Every text pair must reach 4.5:1 and every control, border, and focus pair 3:1 in both themes, including on glass over the brightest (dark theme) or darkest (light theme) part of the wallpaper.

The logo (a teal winged shield with a gold edge) and both wallpapers live in `assets/branding`; the served copies are in `src/ValkyrieID.Server/wwwroot/img` and the admin UI's `public` folder.

The server-rendered and Angular styles use the same token names. Branding overrides keep working: the branding colors apply to the light theme, and optional `Branding:Dark*` colors apply to the dark theme. Operators remain responsible for the contrast of custom colors.

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
- **Glass:** the sign-in card, the account top bar, navigation, and content, and the theme switch are see-through frosted glass (`--vid-glass` with `backdrop-filter: blur()` and a thin `--vid-glass-border`); the wallpaper's lines and nodes show softly through it. The sign-in card adds a faint teal glow in its top-left corner. On glass, muted text, control borders and links use the `--vid-glass-*` variants and inputs a mostly opaque fill, so text keeps 4.5:1 and controls 3:1 over any part of the wallpaper (measured on the rendered pages). Without `backdrop-filter` support, the glass is the plain surface. The Angular admin UI keeps solid surfaces. The see-through glass is tuned and measured for the built-in wallpapers; when a branding background image applies to a theme, `/theme.css` makes that theme's glass mostly opaque (88 % surface), so text keeps its contrast over any image.

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

- Refresh the top bar, branding, user controls, sidebar, active navigation, and content gutters. The sidebar uses the page surface with a thin divider; the active item gets the teal tint and a marker.
- Restyle the theme control added in 11a to fit the new shell.
- Group navigation where that improves scanning without hiding routes from keyboard or screen-reader users.
- Replace awkward mobile navigation wrapping with a responsive navigation pattern that remains discoverable and usable.
- Preserve the skip link, route focus management, live announcements, and current semantic navigation structure.

## Phase 11d: Angular page content and data presentation

- Redesign dashboard metric cards and the recent-activity summary.
- Standardize list, detail, create, and settings page headers and action placement.
- Create reusable patterns for forms, buttons, badges, notices, loading states, empty states, and errors.
- Improve users, clients, API resources, roles, groups, signing keys, and settings pages.
- Make built-in entities, enabled/disabled states, signing-key states, and destructive actions visually distinct.
- Redesign the audit table so the default row is compact and long identifiers/details are available through expansion or a detail view.
- Shorten UUIDs visually while retaining the complete value for accessible text, copying, or inspection.
- Provide usable small-screen alternatives for dense tables, such as stacked rows or expandable cards.

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
