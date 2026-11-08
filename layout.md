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

Phase 11 introduces a new visual identity: **clean and neutral, with a single indigo accent**, in the spirit of tools such as Linear, Vercel, and GitHub. Professional, calm, and quietly polished; color is used for meaning (actions, links, focus, status), not decoration.

Principles:

- neutral gray surfaces; indigo only for primary actions, links, focus, and the active navigation item
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
| `--vid-primary` | `#4f46e5` | `#4f46e5` | Primary buttons (white text) |
| `--vid-primary-hover` | primary mixed with black | primary mixed with black | Primary button hover |
| `--vid-link` | `--vid-primary` | primary mixed with white | Links and accent text |
| `--vid-focus` | `--vid-primary` | primary mixed with white | Focus rings |
| `--vid-accent-tint` | 10 % primary | 18 % primary | Active navigation, selection |
| `--vid-error`, `--vid-ok`, `--vid-warn`, `--vid-info` | `#b91c1c`, `#15803d`, `#a16207`, `#1d4ed8` | `#f87171`, `#4ade80`, `#facc15`, `#93c5fd` | Status text, with matching `-background` tints |

Derived tokens (`--vid-link`, `--vid-focus`, `--vid-primary-hover`, `--vid-accent-tint`) are computed with `color-mix()` from `--vid-primary`, so branding colors carry through. Every text pair must reach 4.5:1 and every control, border, and focus pair 3:1 in both themes.

The logo was designed for the previous navy and orange look; it is kept for now and may get a variant that suits both themes later.

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

The pages must feel like full pages of their own, not a card floating in the middle of the screen (inspired by Pocket ID). Two layouts replace the single centered card. Everything stays server-rendered, works without JavaScript, and keeps the strict CSP (`style-src 'self'`, no script).

### Sign-in layout (split screen)

Used by sign-in, 2FA, recovery code, 2FA enrollment at sign-in, forgot/reset password, email verification and confirmation, sign-out, setup, and the protocol error pages (`ProtocolPages`). The protocol pages have no theme switch: its form would replay the protocol request, and the `form_post` page's `form-action` only allows the client. They still render the stored theme.

- **Desktop (≥ 64rem):** form panel on the left, full height, about 40rem wide. The logo and product name sit at the top, the form is vertically centered, and the theme switch is at the top right of the page, over the art panel. The art panel fills the right side edge to edge, without a border or rounded corners, and fades into the form panel.
- **Art panel:** shows `Branding:BackgroundImage` when set (cover, centered). Without one, a built-in subtle indigo-on-neutral gradient with a large, faint logo, with light and dark variants. It is decorative (`aria-hidden="true"`, no text).
- **Mobile:** the art panel is hidden and the form uses the whole page; no floating card.
- Improve hierarchy, field spacing, action areas, and error/success messages (status tokens with an icon plus text, never color alone). Keep password and 2FA warnings prominent without exposing sensitive data.

### Account layout (app style)

Used by `/account` and its sub-pages (profile, email, password, two-factor, sessions).

- Top bar with the logo and product name, the signed-in user, the theme switch, and Sign out.
- Account navigation (Profile, Email, Password, Two-factor, Sessions) with `aria-current="page"`, and a skip link before it.
- The account home becomes grouped, divided link lists (title, description, decorative chevron) with clear focus/hover states. Account content is flat, not boxed: sections and lists are separated by dividers, and text always sits on the surface color.
- Every page has the same structure: back link, title, short explanation, settings, action area above a divider; destructive actions are in a separate section with a red heading.
- Sessions are a divided list with compact metadata; the current session has a badge and an accent bar.

### Motion (CSS only)

- **No JavaScript and no success interstitial:** after a successful sign-in the server redirects straight to the application.
- **Entry animation:** the art panel slides in from the form side with a gentle image zoom (about 1.15× to 1×), and the form content fades and rises in with a short stagger; about 0.6 s total. It plays only when the server adds a class to `<html>`: on a fresh GET of a flow's entry page (sign-in, setup, forgot password). Never on error re-renders after a POST or on later steps such as 2FA.
- **Step transitions:** cross-document view transitions (`@view-transition { navigation: auto; }`) between our own pages, with the art panel given its own `view-transition-name` so it stays put while the form changes. Browsers without support just navigate.
- The account pages have no entry animation, only view transitions.
- `prefers-reduced-motion: reduce` turns off all animations and view transitions.

### Acceptance

- Integration tests: the entry-animation class only on fresh GETs of entry pages (not after a POST, not on 2FA); both layouts render with and without a background image; the art panel is `aria-hidden`; the account navigation marks the current page.
- The accessibility suite passes on every page in light and dark, at 320 px, and with reduced motion.
- Desktop and mobile screenshots of sign-in, 2FA, setup, account, and sessions in both themes.
- The docs explain that the branding background image fills the art panel.

## Phase 11c: Angular admin shell and navigation

- Refresh the top bar, branding, user controls, sidebar, active navigation, and content gutters. The sidebar uses the page surface with a thin divider; the active item gets the indigo tint and a marker.
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
