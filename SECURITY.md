# Security policy

ValkyrieID is an identity server, so we take security reports seriously and appreciate responsible disclosure.

## Supported versions

ValkyrieID hasn't reached 1.0. Only the **latest release** receives security fixes; upgrade to it before reporting, if you can.

| Version | Supported |
|---|---|
| Latest 0.x release | Yes |
| Older releases | No |

## Reporting a vulnerability

**Don't open a public issue, discussion, or pull request for a vulnerability.**

Report it privately through GitHub: go to the repository's **Security** tab and choose **Report a vulnerability** ([private vulnerability reporting](https://github.com/ValkyrieID/valkyrieid/security/advisories/new)).

Please include:

- the affected version or commit, and your configuration (database, Redis, reverse proxy) where relevant
- steps to reproduce, or a proof of concept
- the impact you expect, and the spec section involved (RFC or OpenID Connect), if any

Never include real user data, tokens, or secrets from a production system.

## What to expect

ValkyrieID is maintained on a best-effort basis, without fixed response times. We'll acknowledge your report as soon as we can, keep you informed while we investigate, and agree on a disclosure date with you. Fixes are published as a new release with a GitHub security advisory, and we credit reporters unless you prefer otherwise.

## Scope

In scope:

- the server: protocol endpoints, sign-in and account pages, the Admin API, and the admin UI
- the `valkyrieid` CLI
- the container image built from this repository
- deviations from the OAuth 2.0 and OpenID Connect specifications that weaken security

Out of scope:

- deployments that ignore the [production checklist](https://valkyrieid.liesel.dev/operations/production-checklist), for example a missing TLS proxy or untrusted forwarded headers
- the [documented spec deviations](PROJECT.md#spec-deviations), unless you show an impact they don't account for
- denial of service through traffic volume, and findings from automated scanners without a demonstrated impact
- vulnerabilities in third-party dependencies that don't affect ValkyrieID (please report those upstream)

## More

The [security documentation](https://valkyrieid.liesel.dev/security/checklist) maps the attacks in RFC 9700 §4 and OpenID Connect Core §16 to ValkyrieID's mitigations and tests, and publishes the OAuch and OWASP ZAP results.
