# OWASP ZAP results

[OWASP ZAP](https://www.zaproxy.org/) scans a throwaway ValkyrieID in Docker on release tags and manual runs (`.github/workflows/zap.yml`). Run the same locally with `security/zap/run.sh` (on Windows `security/zap/run.ps1`; Docker, or Podman behind the Docker CLI).

## Scans

| Scan | What it does |
|---|---|
| Baseline | `zap-baseline.py`: spiders the server and runs the passive rules. |
| API | `zap-api-scan.py` with the Admin API's [OpenAPI document](/openapi/admin-api.json), without a token: every request must be refused. |
| Sign-in pages | A full active scan of the sign-in and account pages (`login-pages.yaml`), starting from a real authorization request. ZAP treats `__RequestVerificationToken` as the anti-CSRF token, so each attack on a form uses a fresh token. |
| Admin API, authenticated | A full active scan of the Admin API from its OpenAPI document (`admin-api.yaml`), with an admin access token on every request. |

The setup:

- ValkyrieID serves TLS from Kestrel at `https://valkyrieid.localtest.me:9443`, with a certificate from a throwaway CA (the same as the [conformance setup](/operations/conformance)). The rate limits are raised through configuration only.
- `run.sh` creates the admin `zapadmin` with a random password through the CLI.
- `security/zap/admin_token.py` (Python standard library only) gets the admin token the way the admin UI does: it signs in with the password, enrolls a TOTP authenticator the first time (admins need two-factor authentication), and runs the authorization code flow with PKCE (`S256`) as `valkyrieid-admin-ui`. Later calls sign in with a TOTP code or reuse the session.
- The ZAP httpsender script `admin-token.js` adds the token to requests to `/api/admin` and renews it well before the 10-minute expiry.
- `check_alerts.py` compares the reports with `security/zap/rules.tsv` and fails on any Medium or High alert that isn't triaged there, and on alerts triaged as `FAIL`. Reports are written to `security/zap/out/`.

## Latest run

| | |
|---|---|
| Date | 2026-11-07 |
| ValkyrieID | commit `f93c74c` with the phase 10d changes (container image, SQLite) |
| ZAP | `ghcr.io/zaproxy/zaproxy:stable` |

No Medium or High findings. One High alert was a false positive. The passive and active rules not listed below found nothing.

| Rule | Alert | Risk | Scans | Triage |
|---|---|---|---|---|
| 10062 | PII Disclosure | High | API | **False positive.** The digits in the random W3C `traceId` of a `401` problem response looked like a card number. No personal data is returned. |
| 10015 | Re-examine Cache-control Directives | Informational | Sign-in pages, Admin API | Responses are `no-store` on purpose. |
| 10049 | Storable / Non-Storable Content | Informational | Baseline, API | Static assets are cacheable; pages and protocol responses are `no-store`. |
| 10104 | User Agent Fuzzer | Informational | Sign-in pages, Admin API | No difference between user agents. |
| 100000 | Client Error response code | Informational | API | Expected `401` and `404` responses to unauthenticated or malformed requests. |
| 10031 | User Controllable HTML Element Attribute | Informational | Sign-in pages | The `ReturnUrl` hidden field echoes the request, HTML-encoded; only local URLs are followed. |
| 10112 | Session Management Response Identified | Informational | Sign-in pages | The session cookie: `__Host-` prefixed, `Secure`, `HttpOnly`, `SameSite=Lax`. |

## Triage

Every alert is listed in `security/zap/rules.tsv` with its rule ID, `IGNORE`, `WARN`, or `FAIL`, and the reason; this page explains them. A new Medium or High alert fails the run until it is fixed or triaged here with a reason.
