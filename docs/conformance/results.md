# Conformance results

ValkyrieID passes the OpenID conformance suite for the Basic OP, Config OP, RP-Initiated Logout, and Back-Channel Logout profiles: every plan runs with no failures, and each result that isn't a pass is expected and explained below. ValkyrieID is not "OpenID Certified"; see [Certification status](/operations/conformance#certification-status).

The plans run in CI on release tags, nightly, and on demand, with the setup described in [Conformance testing](/operations/conformance).

## Latest published run

| | |
|---|---|
| Date | 2026-11-07 |
| ValkyrieID | commit `f93c74c` with the phase 10d changes (container image, SQLite) |
| Suite | `release-v5.3.4` (prebuilt images), run locally with `conformance/run.ps1 -Full` |
| Client authentication | `client_secret_basic`; `client_secret_post` in the extra plans |

| Plan | Variants | Passed | Warning | Review | Skipped | Failed | Log |
|---|---|---|---|---|---|---|---|
| Basic OP | `discovery`, `static_client` | 21 | 6 | 4 | 4 | 0 | [zip](/conformance/2026-11-07/oidcc-basic-certification-test-plan-discovery-static_client-JLo1SN4q7z7NT-07-Nov-2026.zip) |
| Config OP | | 1 | 0 | 0 | 0 | 0 | [zip](/conformance/2026-11-07/oidcc-config-certification-test-plan--8zQylzdCeKRSl-07-Nov-2026.zip) |
| RP-Initiated Logout | `code`, `static_client` | 3 | 0 | 8 | 0 | 0 | [zip](/conformance/2026-11-07/oidcc-rp-initiated-logout-certification-test-plan-code-static_client-aNwehAurlV8KD-07-Nov-2026.zip) |
| Back-Channel Logout | `code`, `static_client` | 2 | 0 | 0 | 0 | 0 | [zip](/conformance/2026-11-07/oidcc-backchannel-rp-initiated-logout-certification-test-plan-code-static_client-U1zi1UNhCOEju-07-Nov-2026.zip) |
| Comprehensive, Basic OP code-flow modules (release tags) | `client_secret_post`, `code` | 7 | 1 | 0 | 0 | 0 | [zip](/conformance/2026-11-07/oidcc-test-plan-client_secret_post-code-default-static_client-kgACpbws65sZh-07-Nov-2026.zip) |
| Form Post Basic (release tags) | `discovery`, `static_client` | 21 | 6 | 4 | 3 | 1 | [zip](/conformance/2026-11-07/oidcc-formpost-basic-certification-test-plan-discovery-static_client-iNJgsRbqUALWe-07-Nov-2026.zip) |

The zips are the suite's signed exports with the full test logs. They contain the client secrets and test user password of the throwaway instance, which was deleted after the run.

## Expected results

These are listed in `conformance/expected-results.json`; any other non-pass result fails the run.

### Warnings

| Test | Reason |
|---|---|
| `oidcc-scope-profile` | Only some profile claims are stored (`name`, `given_name`, `family_name`, `preferred_username`, `updated_at`). OpenID Connect Core §5.4 only asks for claims that are available. |
| `oidcc-scope-email`, `oidcc-alternate-happy-flow` | The claims of the `profile` and `email` scopes are also put in the ID token, for clients that only read the ID token (ASP.NET Core's `AddOpenIdConnect` by default, oauth2-proxy, Grafana). §5.4 returns them from UserInfo when an access token is issued; it's not a MUST. |
| `oidcc-ensure-request-with-acr-values-succeeds` | No authentication context classes are defined; the methods used are in `amr` (RFC 8176). `acr_values` asks for `acr` as a voluntary claim (§3.1.2.1), and `acr` is optional (§2). |
| `oidcc-codereuse-30seconds` | When a code is reused, the refresh tokens issued from it are revoked, but the access token stays valid until it expires, because access tokens are self-contained. Approved deviation from RFC 6749 §4.1.2. |
| `oidcc-claims-essential` | The `claims` request parameter isn't supported (`claims_parameter_supported` is `false`). |

### Reviews

These tests upload a screenshot for a person to check, so the suite can't mark them as passed:

- `oidcc-prompt-login`, `oidcc-max-age-1`: the second sign-in page.
- `oidcc-ensure-registered-redirect-uri`: the error page. ValkyrieID never redirects to an unregistered redirect URI (RFC 6749 §4.1.2.1).
- `oidcc-ensure-request-object-with-redirect-uri`: the error page. The redirect URI is only in the request object, which isn't supported, so the request has none.
- The RP-Initiated Logout tests with an invalid or missing `id_token_hint` or post-logout redirect URI, and those without one: the user confirms the sign-out and sees the signed-out page, which says the application's request was invalid when there is no redirect back (RP-Initiated Logout §3, §4).

### Skipped

- `oidcc-scope-address`, `oidcc-scope-phone`, `oidcc-scope-all`: the `address` and `phone` scopes aren't supported.
- `oidcc-unsigned-request-object-supported-correctly-or-rejected-as-unsupported` (Basic OP): request objects aren't supported (`request_parameter_supported` is `false`).

### Failure in Form Post Basic

`oidcc-unsigned-request-object-supported-correctly-or-rejected-as-unsupported` fails in the Form Post Basic plan only, which runs on release tags and isn't one of the four target profiles. The suite sends `response_mode=form_post` and `state` only inside the request object. ValkyrieID doesn't support request objects and doesn't read the unverified object, so it answers `request_not_supported` (OpenID Connect Core §6.1) with the default `query` response mode, while the suite expects the error to be posted. The same test is skipped in Basic OP.

## Fixes made for the suite

| Test | Problem | Fix |
|---|---|---|
| `oidcc-refresh-token` | Clients must send `prompt=consent` with `offline_access` (OpenID Connect Core §11), and ValkyrieID refused it with `consent_required`. | `prompt=consent` is accepted. There are no consent screens: an administrator allowing a client its scopes is the consent. Approved deviation from §3.1.2.1 (SHOULD prompt). |
