# ValkyrieID UI layout and theme plan

This plan covers the visual refresh of the server-rendered Razor pages and the Angular admin UI. It is tracked as roadmap phase 11 in `PROJECT.md`, before the deferred protocol additions.

## Goals

- Give the login, account, setup, and admin experiences one coherent ValkyrieID visual language.
- Support light, dark, and system themes.
- Improve hierarchy, spacing, navigation, dense data presentation, and responsive behavior without weakening accessibility or the existing security boundaries.
- Preserve the current WCAG 2.2 AA goals, semantic markup, keyboard support, skip navigation, route-focus management, and live announcements.

## Current observations

- The Angular admin shell is functional but visually flat. The navy, purple, and orange palette is a good foundation, but navigation and content hierarchy need more distinction.
- The dashboard metric cards are useful, while the recent-activity table dominates the page and wraps UUIDs and details character-by-character.
- Admin list pages have a consistent basic structure, but create forms, empty states, badges, and explanatory content need stronger visual patterns.
- The audit log needs a compact default view with expandable details rather than displaying all long identifiers and structured data inline.
- The server-rendered login and account pages use a clean but generic centered card. The account landing page is currently a sparse link list.
- `/setup` is currently unavailable because the first administrator already exists. Its source uses the shared Razor layout, so the refresh must still cover the first-run state.
- Lighthouse currently reports 100 accessibility and 100 best practices for the desktop admin dashboard. The refresh must preserve or improve that result.

## Shared design foundation

Create a shared set of CSS custom properties for both UI systems:

- page, surface, elevated-surface, and navigation backgrounds
- primary, primary-hover, accent, and focus colors
- text, muted text, and inverse text
- borders and dividers
- success, warning, error, and informational states
- spacing, radius, shadow, and typography scales

The visual direction should retain the existing ValkyrieID identity:

- deep navy shell and navigation
- purple primary actions
- warm orange active-navigation accent
- calm, warm light background
- dark theme using layered navy/charcoal surfaces rather than pure black

The server-rendered and Angular styles should consume the same token names where possible. Any branding overrides must continue to work and must not silently reduce contrast guarantees.

## Theme behavior

Support three modes:

1. **System** — follow `prefers-color-scheme`.
2. **Light** — explicitly use the light palette.
3. **Dark** — explicitly use the dark palette.

New users should default to System. Add a keyboard-accessible theme control to the Angular top bar and the Razor page shell. Apply the selected mode through a shared `data-theme` attribute, with a preference mechanism that works across `/admin` and the server-rendered pages.

The no-JavaScript default must remain usable. `prefers-color-scheme` should provide the fallback for login, account, and setup pages. Theme controls must have an accessible name and state, and focus/contrast must be verified in every theme.

## Phase 11a: Shared visual foundation and themes

- Define the shared design tokens and light/dark palettes.
- Establish typography, spacing, surface, border, shadow, radius, and status patterns.
- Add theme preference handling and the theme control.
- Apply theme variables to both `site.css` and the Angular global styles.
- Verify contrast, focus indicators, reduced motion, and forced-colors behavior where practical.

## Phase 11b: Razor authentication and account experience

Affected areas include the shared Razor layout, login, logout, error, 2FA, recovery-code, password-reset, email-verification, account, profile, password, two-factor, sessions, and setup pages.

- Refresh the shared branded shell while preserving the strict CSP and no-JavaScript requirement.
- Improve logo/product presentation, page hierarchy, field spacing, action areas, and error/success messages.
- Turn the account landing page into grouped navigation cards with descriptions and clear focus/hover states.
- Use a consistent account-page structure: back/breadcrumb link, title, explanation, form or settings panel, and action area.
- Present sessions as readable metadata cards, highlight the current session, and separate destructive session actions.
- Keep password and 2FA warnings prominent without exposing sensitive data.
- Apply the same theme system to `/account`, `/account/login`, and `/setup`.

## Phase 11c: Angular admin shell and navigation

- Refresh the top bar, branding, user controls, sidebar, active navigation, and content gutters.
- Add the theme control to the admin shell.
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
