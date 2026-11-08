# Configuration

ValkyrieID reads configuration from `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and command-line arguments, in increasing order of precedence.

Nested keys are separated by `:` in JSON and command-line arguments, and by `__` (double underscore) in environment variables. For example, `Database:Provider` becomes `Database__Provider`.

## Runtime settings

Settings marked **runtime** in the tables below can also be changed while the server runs: in the [admin UI](/guide/admin-ui) (**Settings**), through the [Admin API](/reference/admin-api#settings), or with the [`settings` CLI commands](/reference/cli#settings). They are stored in the database and apply without a restart.

- **Configuration takes precedence.** A runtime setting that is set in `appsettings.json`, an environment variable, or on the command line can't be changed at runtime. The admin UI shows it as *set by configuration*, read-only. Remove it from the configuration to manage it in the admin UI.
- **Changes are validated as a whole**, with the same rules as at startup, before anything is saved. Every change is written to the audit log (`settings.updated`) with the old and new values; secrets appear only as `(secret)`.
- **Secrets** (the SMTP password) are encrypted with the [master key](/operations/master-key), never returned by the API, and included in `admin reencrypt`.
- **Other instances and the CLI**: the server that makes a change applies it at once. With [Redis](#redis), other instances are notified and apply it within moments. Every instance also checks for changes every `Settings:ReloadInterval` and applies them then (the fallback if a notification is lost, and the only way without Redis). If stored settings are invalid together with an instance's configuration (for example after an environment variable changed), that instance keeps its previous settings and logs an error; on startup, it refuses to start, like for any invalid configuration. The CLI can still run to fix them (`settings reset`).
- **Already issued** tokens, sessions, and links keep the lifetime they were issued with.

| Key | Default | Description |
|---|---|---|
| `Settings:ReloadInterval` | `00:00:30` | How often each instance checks for settings changed by another instance or the CLI (1 second to 10 minutes). Configuration only. |

## Database

| Key | Default | Description |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Sqlite` or `PostgreSql`. |
| `Database:ConnectionString` | `Data Source=data/valkyrieid.db` | Provider-specific connection string. For SQLite, the directory is created if it doesn't exist. |
| `Database:AutoMigrate` | `true` | Apply pending migrations on startup. Set to `false` to apply migrations manually. |

PostgreSQL example:

```sh
Database__Provider=PostgreSql
Database__ConnectionString="Host=db;Database=valkyrieid;Username=valkyrieid;Password=..."
```

## Redis

Redis is only needed to run [more than one instance](/operations/multi-instance). Without it, ValkyrieID keeps authorization codes and rate-limit counters in memory.

| Key | Default | Description |
|---|---|---|
| `Redis:ConnectionString` | *(none)* | A [StackExchange.Redis configuration string](https://stackexchange.github.io/StackExchange.Redis/Configuration), e.g. `redis:6379,password=...,ssl=true`; for Redis Cluster or Sentinel, list several nodes. Setting it stores authorization codes and rate-limit counters in Redis and notifies other instances of changes. Supply the password through an environment variable or secret store. |
| `Redis:KeyPrefix` | `valkyrieid:` | Prefix for every key and channel, so several deployments can share one Redis. No whitespace or braces (braces would act as a Redis Cluster hash tag). |

When Redis is configured but unreachable, the server fails closed: requests that need it get `503` (or `temporarily_unavailable` at the authorization endpoint), and [readiness](/reference/health) reports unhealthy.

## Master encryption key

| Key | Default | Description |
|---|---|---|
| `MasterKey:Current` | *(required)* | Base64-encoded 256-bit key used to encrypt secrets at rest. |
| `MasterKey:Previous:0`, `:1`, … | *(none)* | Previous keys, still accepted for decryption during rotation. |

The server fails to start if `MasterKey:Current` is missing or invalid. See [Master encryption key](/operations/master-key).

## Issuer

| Key | Default | Description |
|---|---|---|
| `Issuer:Url` | *(required)* | Public issuer URL, e.g. `https://id.example.com`. Must be `https` (plain `http` is accepted only for `localhost` / loopback), without query, fragment, or user info. |

The issuer is published in discovery and in the `iss` claim. Every endpoint URL is derived from it. It is never derived from request headers, so set it to the URL clients use, including when running behind a reverse proxy.

While the issuer is `https`, requests that don't arrive over HTTPS are refused with `400`, except the [health endpoints](/reference/health). Behind a TLS-terminating proxy, configure it as a [trusted proxy](#reverse-proxy).

## HSTS

| Key | Default | Description |
|---|---|---|
| `Hsts:MaxAge` | `365.00:00:00` | `max-age` of the `Strict-Transport-Security` header. |
| `Hsts:IncludeSubDomains` | `false` | Add `includeSubDomains`. Only enable it when every subdomain of the issuer's host serves HTTPS. |
| `Hsts:Preload` | `false` | Add `preload`, for submission to the browsers' preload lists. Requires `Hsts:IncludeSubDomains` and a `MaxAge` of at least 365 days. |

The header is sent on HTTPS responses outside the `Development` environment, and not for `localhost`.

## Reverse proxy

| Key | Default | Description |
|---|---|---|
| `ReverseProxy:KnownProxies:0`, `:1`, … | *(none)* | IP addresses of proxies whose forwarded headers are trusted. |
| `ReverseProxy:KnownNetworks:0`, `:1`, … | *(none)* | Trusted proxy networks in CIDR notation, e.g. `10.0.0.0/8`. |
| `ReverseProxy:ForwardedHeaders` | `XForwardedFor, XForwardedProto` | Forwarded headers to apply. `XForwardedHost` can be added; the issuer is never affected. |
| `ReverseProxy:ForwardLimit` | `1` | How many proxy hops in `X-Forwarded-For` to process. |
| `ReverseProxy:PathBase` | *(none)* | Path prefix to serve under when the proxy doesn't strip it, e.g. `/auth`. Must equal the path of `Issuer:Url`. |

Forwarded headers are ignored unless at least one proxy or network is configured. See [Reverse proxy](/operations/reverse-proxy).

## Admin API and admin UI

| Key | Default | Description |
|---|---|---|
| `AdminApi:Enabled` | `true` | Serve the [Admin API](/reference/admin-api) under `/api/admin`. When `false`, it answers `404`, the built-in `valkyrieid-admin` resource is disabled so no admin tokens can be issued, and the admin UI is off too. The [CLI](/reference/cli) keeps working. |
| `AdminApi:RequireTwoFactor` | `true` | **Runtime.** Admins must sign in with [two-factor authentication](/guide/two-factor) to use the Admin API and the admin UI. Users with the `admin` role who haven't set it up do so at their next sign-in. Access tokens from a sign-in without a second factor get `401` with `error="insufficient_user_authentication"`. |
| `AdminUI:Enabled` | `true` | Serve the [admin UI](/guide/admin-ui) under `/admin`. When `false`, it answers `404` and the built-in `valkyrieid-admin-ui` client is disabled. |

Turning a switch back on re-enables the built-in entries at the next start.

## Tokens

| Key | Default | Description |
|---|---|---|
| `Tokens:AccessTokenLifetime` | `00:30:00` | **Runtime.** Default access token lifetime. Clients can override it. |
| `Tokens:IdTokenLifetime` | `00:05:00` | **Runtime.** Default ID token lifetime. Clients can override it. |
| `Tokens:ClockSkew` | `00:00:30` | Clock skew tolerated when validating tokens (0 to 5 minutes). |
| `Tokens:RefreshTokenSlidingLifetime` | `14.00:00:00` | **Runtime.** A refresh token expires when unused this long. Clients can override it. |
| `Tokens:RefreshTokenAbsoluteLifetime` | `30.00:00:00` | **Runtime.** A refresh token family ends this long after the original sign-in, however often it is refreshed. At least the sliding lifetime. Clients can override it. |

Authorization codes always live 60 seconds and can be redeemed once. They are kept in memory, so a restart invalidates codes that haven't been redeemed yet; the user simply signs in again.

Refresh tokens are stored (hashed) in the database. Families past their absolute lifetime are deleted hourly. See [Refresh tokens](/reference/endpoints#refresh-token-grant).

## Signing keys

| Key | Default | Description |
|---|---|---|
| `SigningKeys:RotationInterval` | `90.00:00:00` | How long a key signs tokens before the next key takes over. |
| `SigningKeys:PublishLead` | `2.00:00:00` | How long a new key is published in JWKS before it is used. |
| `SigningKeys:RsaKeySize` | `2048` | RSA key size in bits (2048 to 8192, multiple of 1024). |
| `SigningKeys:CheckInterval` | `00:05:00` | How often keys are rotated and reloaded. Also the JWKS `Cache-Control` max-age (capped at 1 hour). |

See [Signing key rotation](/operations/key-rotation).

## Passwords

| Key | Default | Description |
|---|---|---|
| `PasswordPolicy:MinimumLength` | `8` | **Runtime.** Minimum length in characters (Unicode code points). At least 8. |
| `PasswordPolicy:MaximumLength` | `128` | **Runtime.** Maximum length (64 to 1024). |
| `PasswordPolicy:RequireUppercase` | `false` | **Runtime.** Require an uppercase letter. |
| `PasswordPolicy:RequireLowercase` | `false` | **Runtime.** Require a lowercase letter. |
| `PasswordPolicy:RequireDigit` | `false` | **Runtime.** Require a digit. |
| `PasswordPolicy:RequireNonAlphanumeric` | `false` | **Runtime.** Require a character that isn't a letter or digit. |
| `PasswordPolicy:CheckBreachedPasswords` | `false` | **Runtime.** Reject passwords that appear in [Have I Been Pwned](https://haveibeenpwned.com/Passwords). |
| `PasswordPolicy:BreachedPasswordApiUrl` | `https://api.pwnedpasswords.com/range/` | Range API base URL, e.g. a self-hosted mirror. Configuration only. |

The defaults follow NIST SP 800-63B, which recommends length and a breached-password check over composition rules. A password can never equal the username or email address.

The breached-password check uses k-anonymity: only the first 5 characters of the password's SHA-1 hash are sent, with response padding enabled. If the service can't be reached within 5 seconds, the password is accepted and a warning is logged.

Passwords are hashed with Argon2id. If the hashing parameters below change, existing hashes keep working and are upgraded at the user's next sign-in.

## Password hashing

| Key | Default | Description |
|---|---|---|
| `PasswordHashing:MemorySizeKiB` | `12288` | Argon2id memory cost in KiB (12 MiB). |
| `PasswordHashing:Iterations` | `3` | Argon2id time cost. |
| `PasswordHashing:Parallelism` | `1` | Argon2id lanes (1 to 16). |

The defaults are one of the configurations recommended by the [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#argon2id). They are all equally strong, trading memory for CPU time:

| Memory | Iterations |
|---|---|
| 47104 KiB (46 MiB) | 1 |
| 19456 KiB (19 MiB) | 2 |
| 12288 KiB (12 MiB) | 3 |
| 9216 KiB (9 MiB) | 4 |
| 7168 KiB (7 MiB) | 5 |

The server refuses to start unless memory and iterations are each at least those of one row. Higher values are allowed, up to 1 GiB and 100 iterations.

Each password check briefly uses the configured memory, so peak memory grows with the number of concurrent sign-ins (e.g. 50 at once with the defaults need about 600 MiB). Size your instances for your peak sign-in rate, or choose a lower-memory row. The [rate limits](#rate-limits) bound how much a single client or account can add.

## Lockout

| Key | Default | Description |
|---|---|---|
| `Lockout:MaxFailedAttempts` | `5` | **Runtime.** Consecutive failed sign-in attempts (wrong passwords and wrong 2FA or recovery codes) that lock an account. |
| `Lockout:Duration` | `00:15:00` | **Runtime.** How long the account stays locked. |

While an account is locked, every login attempt fails with the same message as a wrong password. Unlock an account early with [`admin unlock`](/reference/cli#admin-recovery).

For users with 2FA, the failure count is only reset once the whole sign-in succeeds, so entering the password again doesn't give more attempts at the code.

## Two-factor authentication

| Key | Default | Description |
|---|---|---|
| `TwoFactor:Required` | `false` | **Runtime.** Require 2FA for every user. Users who haven't set it up must do so at their next sign-in. |

Admins can also require it per user with [`user require-2fa`](/reference/cli#users). Users with the `admin` role need it while [`AdminApi:RequireTwoFactor`](#admin-api-and-admin-ui) is on (the default). See [Two-factor authentication](/guide/two-factor).

## Session

| Key | Default | Description |
|---|---|---|
| `Session:Lifetime` | `08:00:00` | **Runtime.** Absolute lifetime of the single sign-on session. It isn't extended by activity. Applies to new sessions. |

The session cookie is `__Host-valkyrie.session` (`Secure`, `HttpOnly`, `SameSite=Lax`). It is a browser-session cookie, so it is also dropped when the browser closes. Each session is stored in the database and checked on every request, so revoking it, disabling the user, resetting the password or changing roles signs the user out immediately.

Because the cookie is `Secure`, sign-in only works over HTTPS (or `http://localhost` in browsers that treat it as secure).

Administrators list and revoke sessions with the [`session` commands](/reference/cli#sessions).

## Back-channel logout

| Key | Default | Description |
|---|---|---|
| `BackChannelLogout:RequestTimeout` | `00:00:05` | Timeout of each request to a client (1 to 60 seconds). A sign-out waits at most this long for the clients before redirecting the browser. |
| `BackChannelLogout:RetryDelays` | `00:00:10`, `00:00:30`, `00:02:00`, `00:10:00`, `01:00:00` | Delays before each retry after a network error, timeout, `408`, `429`, or `5xx`. After the last one the notification is abandoned. Set as an array, e.g. `BackChannelLogout__RetryDelays__0=00:00:15`; at most 20 entries. |
| `BackChannelLogout:PollInterval` | `00:01:00` | How often each instance checks for pending notifications queued by another process, such as the CLI or another instance. Notifications queued by the instance itself are sent right away; with [Redis](#redis), other instances are woken right away too. |

See [Back-channel logout](/reference/endpoints#back-channel-logout).

## SMTP

Email features ([password reset, email verification, email change](/guide/email)) are turned on by setting `Smtp:Host`.

| Key | Default | Description |
|---|---|---|
| `Smtp:Host` | *(none)* | **Runtime.** The SMTP server. Unset turns the email features off. |
| `Smtp:Port` | `587` | **Runtime.** |
| `Smtp:Security` | `StartTls` | **Runtime.** `StartTls` (upgrade the connection; fail if the server doesn't offer it), `Tls` (TLS from the start, usually port 465), or `None` (only allowed for `localhost` or a loopback address). The server certificate is always validated. |
| `Smtp:UserName` / `Smtp:Password` | *(none)* | **Runtime.** Credentials; set both or neither. In configuration, supply the password through an environment variable or secret store; set at runtime, it is stored encrypted with the master key. |
| `Smtp:FromAddress` | *(none)* | **Runtime.** Sender address. Required when `Smtp:Host` is set. |
| `Smtp:FromName` | `Branding:ProductName` | **Runtime.** Sender display name. |
| `Smtp:Timeout` | `00:00:30` | Timeout of each SMTP operation (1 second to 5 minutes). |
| `Smtp:RetryDelays` | `00:00:30`, `00:02:00`, `00:10:00`, `01:00:00` | Delays before each retry after a network error, timeout, or `4xx` reply. After the last one the email is abandoned. Set as an array, e.g. `Smtp__RetryDelays__0=00:01:00`; at most 20 entries. |
| `Smtp:PollInterval` | `00:01:00` | How often each instance checks for emails queued by another process (the CLI or another instance). With [Redis](#redis), instances are also woken right away. |

Check the settings with **Send test email** on the admin UI's SMTP settings page, or with [`admin send-test-email`](/reference/cli#admin-recovery).

## Email links

| Key | Default | Description |
|---|---|---|
| `Email:VerificationAtSignIn` | `Off` | **Runtime.** After sign-in, users whose email address isn't verified see a verification page before returning to the application: `Prompt` offers it once per sign-in, `Require` lets them continue only once the address is verified. Only applies while SMTP is configured; see [Email](/guide/email#verification-at-sign-in). |
| `Email:PasswordResetLifetime` | `00:30:00` | **Runtime.** How long a password reset link works (5 minutes to 24 hours). |
| `Email:VerificationLifetime` | `1.00:00:00` | **Runtime.** How long email verification and email change links work (5 minutes to 7 days). |
| `Email:ResendInterval` | `00:01:00` | At most one email of each kind per account in this interval (0 to 1 hour); further requests are ignored silently. |

## Rate limits

| Key | Default | Description |
|---|---|---|
| `RateLimits:Login:PermitLimit` / `:Window` | `10` / `00:01:00` | Login and setup form submissions per client IP address. |
| `RateLimits:LoginAccount:PermitLimit` / `:Window` | `10` / `00:15:00` | Login attempts per username or email, whether or not the account exists. |
| `RateLimits:Token:PermitLimit` / `:Window` | `100` / `00:01:00` | Token, userinfo, revocation, and introspection endpoint requests per client IP address. |
| `RateLimits:Authorize:PermitLimit` / `:Window` | `60` / `00:01:00` | Authorization endpoint requests per client IP address. |
| `RateLimits:TwoFactor:PermitLimit` / `:Window` | `10` / `00:01:00` | 2FA code and recovery code submissions (at sign-in and when setting up 2FA), and the account forms that ask for the current password, per client IP address. |
| `RateLimits:Email:PermitLimit` / `:Window` | `5` / `00:05:00` | Submissions of the [email](/guide/email) forms (forgot password, reset password, verify email, change email) per client IP address. |
| `RateLimits:Account:PermitLimit` / `:Window` | `30` / `00:01:00` | Other account form submissions (profile, sessions, sign-out, theme) per client IP address. |

Rejected requests get `429 Too Many Requests` with a `Retry-After` header. IPv6 clients are counted per `/64` prefix, since a single client usually has a whole `/64` to choose addresses from. Without Redis, counters are kept in memory per instance; with [Redis](#redis), all instances share them, so the limits apply to the whole deployment. If Redis is unreachable, these requests get `503 Service Unavailable` with `Retry-After` instead of being counted per instance. Behind a reverse proxy, configure [trusted proxies](#reverse-proxy) so limits apply per client rather than per proxy.

## Branding

| Key | Default | Description |
|---|---|---|
| `Branding:ProductName` | `ValkyrieID` | **Runtime.** Shown in page titles and in place of a logo. |
| `Branding:Directory` | *(none)* | Directory with branding files, served under `/branding/`. Configuration only. |
| `Branding:Logo` | *(none)* | **Runtime.** Logo file name in `Branding:Directory`. Without one, the ValkyrieID logo is shown next to the product name. |
| `Branding:BackgroundImage` | *(none)* | **Runtime.** Image file name in `Branding:Directory`. It fills the art panel next to the sign-in form (see [Page layouts](#page-layouts)); without one, a subtle built-in gradient with the logo is shown. |
| `Branding:CustomCss` | *(none)* | **Runtime.** Stylesheet file name in `Branding:Directory`, loaded after the built-in styles. |
| `Branding:PrimaryColor` | `#4f46e5` | **Runtime.** Buttons, links, and focus outlines in the light theme. |
| `Branding:BackgroundColor` | `#fafafa` | **Runtime.** Page background in the light theme. |
| `Branding:SurfaceColor` | `#ffffff` | **Runtime.** Card background in the light theme. |
| `Branding:TextColor` | `#18181b` | **Runtime.** Text in the light theme. |
| `Branding:DarkPrimaryColor` | `#4f46e5` | **Runtime.** Buttons in the dark theme; links and focus outlines use a lighter tint of it. |
| `Branding:DarkBackgroundColor` | `#0a0a0b` | **Runtime.** Page background in the dark theme. |
| `Branding:DarkSurfaceColor` | `#141416` | **Runtime.** Card background in the dark theme. |
| `Branding:DarkTextColor` | `#ededef` | **Runtime.** Text in the dark theme. |

Colors must be hex (`#4f46e5`), named (`teal`), `rgb()` or `hsl()` values. File names must be plain names without paths; at runtime, they are chosen from the images (`.png`, `.jpg`, `.gif`, `.webp`, `.avif`, `.svg`, `.ico`) and stylesheets (`.css`) in `Branding:Directory`. The server doesn't start if a value is invalid.

### Themes

The sign-in, account, and setup pages and the admin UI have a light and a dark theme. By default they follow the operating system or browser setting (`prefers-color-scheme`); a theme switch at the top right of the pages (and in the admin UI's top bar) lets users choose System, Light, or Dark. The choice is stored in the `__Host-valkyrie.theme` cookie for one year and applies to both the pages and the admin UI. The pages render it on the server, so it works without JavaScript; the switch there is a form that posts to [`POST /account/theme`](/reference/endpoints#theme).

The colors without `Dark` apply to the light theme only, and the `Dark` colors to the dark theme only, so a light branding can't make the dark theme unreadable. Unset colors keep the built-in palette of that theme.

The Content Security Policy only allows styles and images from the server itself, so branding files must be in `Branding:Directory`. A custom stylesheet can override the CSS variables `--vid-primary`, `--vid-primary-text`, `--vid-background`, `--vid-surface`, `--vid-text`, `--vid-muted`, `--vid-divider`, `--vid-error`, `--vid-error-background` and `--vid-border`. To change only the dark theme, use the selectors `:root[data-theme="dark"]` and, inside `@media (prefers-color-scheme: dark)`, `:root:not([data-theme="light"])`. Keep a contrast ratio of at least 4.5:1 for text (WCAG 2.2 AA) in both themes. The primary color needs 4.5:1 against white (button text), and in the light theme against the surface color (links); see [Accessibility: Branding](/accessibility/#branding) for every variable's requirement.

### Page layouts

The sign-in pages (sign-in, two-factor and recovery codes, 2FA enrollment, password reset, email verification, sign-out, setup, and sign-in errors) use a split layout: the form on the left, and an art panel filling the right side. The art panel shows `Branding:BackgroundImage`, scaled to cover the panel and centered, so choose an image that still works when cropped; without one, it shows a subtle gradient with a large, faint logo in the light or dark variant. The panel is decorative (hidden from screen readers), so don't put information in the image. On narrow screens (below 64rem) the art panel is hidden and the form uses the whole page.

The account pages use an app layout with a top bar (product, signed-in user, theme switch, Sign out) and an account navigation.

The sign-in, setup, and forgot-password pages play a short entry animation when they are first opened, and moving between pages uses a cross-fade (CSS view transitions) in browsers that support it. Both are CSS only and are turned off when the user prefers reduced motion.

## Audit log

| Key | Default | Description |
|---|---|---|
| `AuditLog:Retention` | `365.00:00:00` | **Runtime.** Entries older than this are deleted. `00:00:00` keeps them forever; otherwise 1 to 3650 days. |
| `AuditLog:CleanupInterval` | `01:00:00` | How often old entries are deleted (1 minute to 1 day). Each deletion is recorded as `audit.purged`. |

## Logging

Logs are written to the console as JSON by default (`Logging:Console:FormatterName` = `json`). The `Development` environment uses the human-readable `simple` formatter. Log levels use the standard [`Logging:LogLevel`](https://learn.microsoft.com/aspnet/core/fundamentals/logging/) settings.

## OpenTelemetry

| Key | Default | Description |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none)* | OTLP endpoint. OpenTelemetry is enabled only when this is set. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | *(none)* | Extra headers, e.g. for authentication. |

The other standard `OTEL_*` environment variables are also supported. See [Observability](/operations/observability).
