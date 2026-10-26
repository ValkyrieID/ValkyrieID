# AGENTS.md

Guidance for AI coding agents working in this repository. The full specification lives in [`PROJECT.md`](PROJECT.md); read it before making design decisions. If this file and `PROJECT.md` conflict, `PROJECT.md` wins. Point out the conflict.

## Project

ValkyrieID is a minimal, lightweight OpenID Connect / OAuth 2.0 identity server.

- Backend: .NET 10, ASP.NET Core, C# 14. **The protocol layer is written from scratch**; do not add OpenIddict, Duende IdentityServer, or similar libraries.
- Storage: EF Core with SQLite and PostgreSQL; Redis only for multi-instance deployments.
- Login/account pages: server-rendered Razor Pages (no SPA, no JavaScript required).
- Admin UI: Angular (latest), served by the ASP.NET Core app, talking only to the Admin REST API.
- Docs: VitePress in `/docs`, published to https://valkyrieid.liesel.dev.
- Work follows the phased roadmap in `PROJECT.md` §14. Don't build features from later phases unless asked.
- When an item is moved to an earlier or later phase, record it in `PROJECT.md` §14 (roadmap markers and the "Roadmap changes" table) in the same change.

## Rules

### Dependencies

- **Never hand-edit package references or versions.** Add packages through the CLI:
  - .NET: `dotnet add <project> package <name>` (Central Package Management writes the version to `Directory.Packages.props`).
  - Angular: `npm install <name>` or `ng add <name>`.
- Always use the **latest stable** version. Check with `dotnet list package --outdated` / `npm outdated`.
- Keep dependencies minimal; prefer the BCL and ASP.NET Core built-ins. Ask before adding a new dependency that isn't obviously needed.
- Commit lock files (`packages.lock.json`, `package-lock.json`). If restore fails in locked mode, re-restore with the CLI. Don't edit lock files by hand.

### .NET code

- Builds use `TreatWarningsAsErrors`. **Fix warnings; don't suppress them.** Use `#pragma` or `<NoWarn>` only with a comment explaining why, and only when unavoidable.
- Use the latest C# syntax: file-scoped namespaces, primary constructors, collection expressions, `field` keyword, extension members, pattern matching, raw string literals.
- Nullable reference types are enabled; don't use `!` to silence warnings without a reason.
- Shared build settings belong in `Directory.Build.props`; don't duplicate them in project files.
- Use `TimeProvider` for time (testable clock skew, lifetimes, TOTP).
- Use `async` all the way down and pass `CancellationToken` through.

### Angular code

- **Signals for all state:** `signal`, `computed`, `input()`, `output()`, `model()`, `resource` / `httpResource`. No `@Input()` / `@Output()` decorators, and no RxJS-held component state.
- **`ChangeDetectionStrategy.OnPush` on every component.**
- **Zoneless** app; don't add zone.js.
- Standalone components, `@if` / `@for` / `@switch`, `inject()` instead of constructor injection.
- TypeScript `strict`; no `any`.
- Target WCAG 2.2 AA: labels, keyboard navigation, contrast.

### Formatting and linting

Run these before finishing a change; CI fails otherwise:

```sh
dotnet format
npx prettier --write .     # in the Angular project
npm run lint               # in the Angular project
```

## Security-critical rules

This is an identity server. Treat every change to protocol, authentication, token, or crypto code as security-sensitive.

- Follow the RFCs/specs referenced in `PROJECT.md` exactly. When unsure about spec behavior, quote the relevant section in your reasoning or a code comment instead of guessing.
- **Spec compliance is mandatory.** ValkyrieID must follow the OpenID Connect and OAuth 2.0 specifications (e.g. RFC 6749, RFC 6750, RFC 7517/7518/7519, RFC 7636, RFC 8414, RFC 9068, RFC 9207, RFC 9700, OIDC Core/Discovery/Logout). If a user decision or instruction contradicts a spec, don't implement it: point out the conflict, cite the relevant section, and revisit the decision with the user.
- **Redirect URIs:** exact string match only; the only exception is loopback (`127.0.0.1` / `[::1]`) with any port. Never redirect to an unvalidated URI, including on errors.
- **Secrets at rest:**
  - Hash client secrets, refresh tokens, authorization codes, and recovery codes.
  - Encrypt signing keys, TOTP secrets, and the SMTP password with the master key.
  - Never log any of them.
- **Comparisons:** use `CryptographicOperations.FixedTimeEquals` for secrets, codes, and hashes.
- **Passwords:** Argon2id only.
- **Randomness:** use `RandomNumberGenerator` for anything security-related.
- **No user enumeration:** keep error messages generic and verification constant-time, including for unknown users.
- **PKCE:** `S256` only; required for public clients.
- **Authorization codes:** single-use, 60 s lifetime.
- **Refresh tokens:** rotate on use; reuse revokes the whole token family.
- **`sub` claim:** an immutable GUID, never the username or email.
- **Issuer:** always the configured public issuer URL, never derived from request headers.
- **Never log** tokens, passwords, secrets, or full authorization request URLs containing codes.
- **Write audit log entries** for authentication events and all admin changes.
- **Tests:** add tests for both the success path and the attack/abuse cases, e.g.:
  - code replay
  - refresh token reuse
  - wrong redirect URI
  - PKCE mismatch
  - expired tokens and clock skew boundaries

## Testing

- xUnit for unit and integration tests (`WebApplicationFactory`).
- Integration tests run against **SQLite** and **PostgreSQL** (Testcontainers; requires Docker).
- Angular: unit tests for components and services.
- Every bug fix includes a regression test. Every protocol feature includes integration tests over HTTP.
- Run before finishing:

```sh
dotnet build
dotnet test
npm test        # in the Angular project
```

## Database

- Every schema change needs EF Core migrations for **both** SQLite and PostgreSQL.
- Don't edit existing migrations once they're merged; add a new one.
- Don't write provider-specific SQL unless isolated behind the provider abstraction.

## Documentation

- Any change to configuration, endpoints, or behavior includes the matching update in `/docs` in the same change.
- Keep the README short; detailed content belongs in `/docs`.
- If a decision changes the specification, update `PROJECT.md` too.

## Working style

- Make small, focused changes that match the existing structure and naming.
- Ask before:
  - making architectural changes
  - adding dependencies
  - deviating from `PROJECT.md`
- When asking the user questions or offering choices, use the built-in question tool instead of listing questions in prose.
- Don't commit or push unless asked.
