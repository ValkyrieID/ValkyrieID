# OWASP ZAP results

[OWASP ZAP](https://www.zaproxy.org/) scans a throwaway ValkyrieID in Docker on release tags and manual runs (`.github/workflows/zap.yml`). Run the same locally with `security/zap/run.sh` (on Windows `security/zap/run.ps1`; Docker, or Podman behind the Docker CLI).

## Scans

| Scan | What it does |
|---|---|
| Baseline | `zap-baseline.py`: spiders the server and runs the passive rules. |
| API | `zap-api-scan.py` with the Admin API's [OpenAPI document](/openapi/admin-api.json), without a token: every request must be refused. |
| Sign-in pages | A full active scan of the sign-in and account pages (`login-pages.yaml`), starting from a real authorization request. ZAP treats `__RequestVerificationToken` as the anti-CSRF token, so each attack on a form uses a fresh token. A spider job per page submits its forms, so every form POST is in the scan. |
| Admin API, authenticated | A full active scan of the Admin API from its OpenAPI document (`admin-api.yaml`), with an admin access token on every request. |

The setup:

- ValkyrieID serves TLS from Kestrel at `https://valkyrieid.localtest.me:9443`, with a certificate from a throwaway CA (the same as the [conformance setup](/operations/conformance)). The rate limits and the lockout threshold are raised through configuration only. SMTP points to a host that doesn't exist (`smtp.invalid`), which turns on the email pages (forgot and reset password, email confirmation) without delivering anything.
- `run.sh` creates the admins `zapadmin` and `zapenroll` with a random password through the CLI.
- `security/zap/admin_token.py` (Python standard library only) gets the admin token the way the admin UI does: it signs in with the password, enrolls a TOTP authenticator the first time (admins need two-factor authentication), and runs the authorization code flow with PKCE (`S256`) as `valkyrieid-admin-ui`. Later calls sign in with a TOTP code or reuse the session.
- The ZAP httpsender script `admin-token.js` adds the token to requests to `/api/admin` and renews it well before the 10-minute expiry.
- The two-factor, recovery code, and enrollment pages only show their forms during a sign-in that stopped after the password. The httpsender script `pending-login.js` gets that pending-login cookie from `admin_token.py --pending` (`zapadmin`, with an authenticator, for the two-factor and recovery code pages; `zapenroll`, without one, for enrollment) and adds it to requests to those pages.
- The sign-in scan writes its sites tree to `out/login-pages-tree.yaml`, to check which pages and form POSTs were scanned. Not reached: the reset password form, which needs a valid emailed token, and the sign-out form, which needs a session. The integration tests cover both.
- `check_alerts.py` compares the reports with `security/zap/rules.tsv` and fails on any Medium or High alert that isn't triaged there, and on alerts triaged as `FAIL`. Reports are written to `security/zap/out/`.

## Latest run

| | |
|---|---|
| Date | 2026-11-08 |
| ValkyrieID | commit `feae35d` (container image, SQLite), with the sign-in scan setup described above |
| ZAP | `ghcr.io/zaproxy/zaproxy:stable` |

No Medium or High findings; only informational alerts. The passive and active rules not listed below found nothing. The sign-in scan covered the GET and form POST of the sign-in, two-factor, recovery code, enrollment, forgot password, email confirmation, and email verification pages, the reset password and sign-out pages (GET), and the authorize and end session requests.

| Rule | Alert | Risk | Scans | Triage |
|---|---|---|---|---|
| 10015 | Re-examine Cache-control Directives | Informational | Sign-in pages, Admin API | Responses are `no-store` on purpose. |
| 10024 | Information Disclosure - Sensitive Information in URL | Informational | Sign-in pages | The `token` of the one-time links in verification and password reset emails. The pages are `no-store` with `Referrer-Policy: no-referrer`; GET only shows a confirmation form, and the token is used by the POST. |
| 10031 | User Controllable HTML Element Attribute | Informational | Sign-in pages | The `ReturnUrl` hidden field echoes the request, HTML-encoded; only local URLs are followed. |
| 10104 | User Agent Fuzzer | Informational | Sign-in pages, Admin API | No difference between user agents. |
| 10112 | Session Management Response Identified | Informational | Sign-in pages | The session cookie: `__Host-` prefixed, `Secure`, `HttpOnly`, `SameSite=Lax`. |
| 100000 | Client Error response code | Informational | API | Expected `401` and `404` responses to unauthenticated or malformed requests. |

Earlier runs also reported rule 10062 (PII Disclosure, High) as a **false positive**: the digits in the random W3C `traceId` of a `401` problem response looked like a card number. No personal data is returned. Rule 10049 (Storable / Non-Storable Content, Informational): static assets are cacheable; pages and protocol responses are `no-store`. Both stay triaged in `rules.tsv`.

## Triage

Every alert is listed in `security/zap/rules.tsv` with its rule ID, `IGNORE`, `WARN`, or `FAIL`, and the reason; this page explains them. A new Medium or High alert fails the run until it is fixed or triaged here with a reason.
