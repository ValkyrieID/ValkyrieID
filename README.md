# ValkyrieID

<img src="assets/branding/ValkyrieID.png" alt="ValkyrieID logo, a winged shield" width="128" height="128">

A minimal, lightweight OpenID Connect / OAuth 2.0 identity server built on .NET.

> **Pre-1.0.** Configuration and APIs may change between 0.x releases; read the [changelog](CHANGELOG.md) before upgrading.

## Features

- **OpenID Connect and OAuth 2.0:** authorization code with PKCE, client credentials, refresh tokens with rotation and reuse detection, revocation, introspection, RP-initiated and back-channel logout. Passes the OpenID conformance plans for Basic OP, Config OP, and both logout profiles.
- **Users:** Argon2id passwords, TOTP two-factor authentication with recovery codes, self-service account pages, email verification and password reset.
- **Administration:** a first-run setup wizard, an admin UI, a REST API, a CLI, and an audit log.
- **Small to run:** a single, non-root container with SQLite, or PostgreSQL and Redis for several instances.
- **Secure by default:** HTTPS only, exact redirect URI matching, secrets hashed or encrypted at rest, strict CSP, and checked against RFC 9700.
- **Accessible:** server-rendered sign-in pages without JavaScript, light and dark themes, branding, and WCAG 2.2 AA.

## Quick start

ValkyrieID only runs over HTTPS, also locally. The [Docker quick start](https://valkyrieid.liesel.dev/guide/docker#quick-start) runs it behind Caddy at `https://localhost:8443` with a Compose file. Until the first release is published, build the image first:

```sh
docker build -t ghcr.io/valkyrieid/valkyrieid .
```

Then open `https://localhost:8443/setup` to create the first administrator. To run from source, see [Getting started](https://valkyrieid.liesel.dev/guide/getting-started).

## Documentation

**https://valkyrieid.liesel.dev**: guides, configuration and API reference, the [production checklist](https://valkyrieid.liesel.dev/operations/production-checklist), and how to [contribute and run the tests](https://valkyrieid.liesel.dev/guide/getting-started#development). The docs source is in [`docs/`](docs/), and the specification and roadmap are in [`PROJECT.md`](PROJECT.md).

## Security

Please report vulnerabilities privately; see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
