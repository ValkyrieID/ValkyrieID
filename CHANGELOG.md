# Changelog

All notable changes to ValkyrieID are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Before 1.0, a minor version (0.x) may change configuration or behavior; read this file before upgrading.

## [Unreleased]

The first release, v0.1.0, will be cut from this section ([roadmap](PROJECT.md#14-roadmap), phase 12).

### Added

- **Protocol** (OAuth 2.0, OpenID Connect):
  - `authorization_code` with PKCE (`S256` only, required for public clients), `client_credentials`, and `refresh_token` grants
  - discovery and JWKS; JWT access tokens (RFC 9068) and ID tokens signed with RS256; userinfo
  - `prompt`, `max_age`, `nonce`, `login_hint`, `form_post` response mode, and the `iss` authorization response parameter (RFC 9207)
  - refresh token rotation with reuse detection that revokes the whole family; sliding and absolute lifetimes; `offline_access`
  - token revocation (RFC 7009) and introspection (RFC 7662)
  - RP-initiated logout and back-channel logout with retries
  - exact redirect URI matching, with any port only for `127.0.0.1` / `[::1]` (RFC 8252); CORS per client
  - passes the OpenID conformance plans for Basic OP, Config OP, RP-Initiated Logout, and Back-Channel Logout
- **Clients and APIs:** confidential and public clients with multiple, optionally expiring secrets (stored hashed); per-client lifetimes; API resources and scopes, with secrets for introspection.
- **Signing keys:** automatic RSA key rotation with pre-publication, coordinated across instances by a database lease.
- **Users and sign-in:**
  - Argon2id password hashing, a configurable password policy, and an optional breached-password check (k-anonymity)
  - server-side SSO sessions with `sid`, lockout, rate limits, and no user enumeration
  - TOTP two-factor authentication with recovery codes, enforceable globally, per user, and for admins (`amr` per RFC 8176)
  - self-service account pages: profile, password, 2FA, sessions, email
  - email verification, password reset, and email change over SMTP
- **Administration:**
  - first-run setup wizard
  - Admin REST API (users, roles, groups, custom claims, clients, API resources, signing keys, sessions, settings, audit log) with an OpenAPI document
  - Angular admin UI, signed in through ValkyrieID itself with a public PKCE client
  - runtime settings stored in the database, with configuration taking precedence
  - CLI for users, sessions, clients, resources, settings, and break-glass recovery (`admin reset-password`, `reset-2fa`, `grant-admin`, `reencrypt`); `--help` and `--version`
  - audit log of authentication events and admin changes, with retention
- **Security:**
  - master encryption key (AES-256-GCM) for signing keys, TOTP secrets, Data Protection keys, queued emails, and the SMTP password, with rotation and `admin reencrypt`
  - strict CSP and security headers, `__Host-` cookies, HSTS, 303 redirects after posts
  - reviewed against RFC 9700 §4 and OIDC Core §16 ([security checklist](docs/security/checklist.md)); scanned with OWASP ZAP and OAuch
- **Operations:**
  - SQLite or PostgreSQL with automatic migrations
  - multiple instances with PostgreSQL and Redis (authorization codes, rate limits, change notifications), failing closed while Redis is unavailable
  - reverse proxy support (trusted proxies, forwarded headers, path base)
  - health endpoints, JSON logs, and OpenTelemetry
  - chiseled, non-root container image for amd64 and arm64, with a built-in `healthcheck` command
- **User interface:** server-rendered sign-in and account pages that work without JavaScript; light, dark, and system themes; branding (logo, wallpapers, colors, custom CSS); WCAG 2.2 AA, checked with axe, a contrast script, and Lighthouse.
- **Documentation** at <https://valkyrieid.liesel.dev>, including a production checklist, backups, and upgrades.

### Security

- The issuer must be an `https` URL, also on loopback hosts (OIDC Core §2, OIDC Discovery §3, RFC 8414 §2). The server refuses to start with an `http` issuer. Local setups use HTTPS too (see the [Docker quick start](https://valkyrieid.liesel.dev/guide/docker#quick-start)).

### Fixed

- `valkyrieid --help`, `--version`, or a mistyped command no longer start the server or migrate the database.

### Known limitations

- **Deliberate deviations** from SHOULD/RECOMMENDED requirements are listed in [PROJECT.md](PROJECT.md#spec-deviations), for example: access tokens can't be revoked, `prompt=consent` shows no consent screen, and there is no dynamic client registration.
- **No sender-constrained tokens** (DPoP) and no asymmetric client authentication (`private_key_jwt`).
- **Migrations only go forward.** Rolling back means restoring a backup, and an older version doesn't detect a database migrated by a newer one ([Upgrades](https://valkyrieid.liesel.dev/operations/upgrades)).
- **No container image published yet**; build it from the repository until v0.1.0.
- The version is the .NET default (`1.0.0`) until the release workflow sets it.

### Deferred

- Phase 13a: `private_key_jwt` client authentication (OIDC Core §9, RFC 7523).
- Phase 13b: DPoP (RFC 9449).

[Unreleased]: https://github.com/ValkyrieID/valkyrieid/commits/main
