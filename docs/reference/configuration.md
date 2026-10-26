# Configuration

ValkyrieID reads configuration from `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and command-line arguments, in increasing order of precedence.

Nested keys are separated by `:` in JSON and command-line arguments, and by `__` (double underscore) in environment variables. For example, `Database:Provider` becomes `Database__Provider`.

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

## Tokens

| Key | Default | Description |
|---|---|---|
| `Tokens:AccessTokenLifetime` | `00:30:00` | Default access token lifetime. Clients can override it. |
| `Tokens:IdTokenLifetime` | `00:05:00` | Default ID token lifetime. Clients can override it. |
| `Tokens:ClockSkew` | `00:00:30` | Clock skew tolerated when validating tokens (0 to 5 minutes). |

Authorization codes always live 60 seconds and can be redeemed once. They are kept in memory, so a restart invalidates codes that haven't been redeemed yet; the user simply signs in again.

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
| `PasswordPolicy:MinimumLength` | `8` | Minimum length in characters (Unicode code points). At least 8. |
| `PasswordPolicy:MaximumLength` | `128` | Maximum length (64 to 1024). |
| `PasswordPolicy:RequireUppercase` | `false` | Require an uppercase letter. |
| `PasswordPolicy:RequireLowercase` | `false` | Require a lowercase letter. |
| `PasswordPolicy:RequireDigit` | `false` | Require a digit. |
| `PasswordPolicy:RequireNonAlphanumeric` | `false` | Require a character that isn't a letter or digit. |
| `PasswordPolicy:CheckBreachedPasswords` | `false` | Reject passwords that appear in [Have I Been Pwned](https://haveibeenpwned.com/Passwords). |
| `PasswordPolicy:BreachedPasswordApiUrl` | `https://api.pwnedpasswords.com/range/` | Range API base URL, e.g. a self-hosted mirror. |

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
| `Lockout:MaxFailedAttempts` | `5` | Consecutive failed logins that lock an account. |
| `Lockout:Duration` | `00:15:00` | How long the account stays locked. |

While an account is locked, every login attempt fails with the same message as a wrong password. Unlock an account early with [`admin unlock`](/reference/cli#admin-recovery).

## Session

| Key | Default | Description |
|---|---|---|
| `Session:Lifetime` | `08:00:00` | Absolute lifetime of the single sign-on session. It isn't extended by activity. |

The session cookie is `__Host-valkyrie.session` (`Secure`, `HttpOnly`, `SameSite=Lax`). It is a browser-session cookie, so it is also dropped when the browser closes. Each session is stored in the database and checked on every request, so revoking it, disabling the user, resetting the password or changing roles signs the user out immediately.

Because the cookie is `Secure`, sign-in only works over HTTPS (or `http://localhost` in browsers that treat it as secure).

## Rate limits

| Key | Default | Description |
|---|---|---|
| `RateLimits:Login:PermitLimit` / `:Window` | `10` / `00:01:00` | Login and setup form submissions per client IP address. |
| `RateLimits:LoginAccount:PermitLimit` / `:Window` | `10` / `00:15:00` | Login attempts per username or email, whether or not the account exists. |
| `RateLimits:Token:PermitLimit` / `:Window` | `100` / `00:01:00` | Token and userinfo endpoint requests per client IP address. |
| `RateLimits:Authorize:PermitLimit` / `:Window` | `60` / `00:01:00` | Authorization endpoint requests per client IP address. |

Rejected requests get `429 Too Many Requests` with a `Retry-After` header. Counters are kept in memory per instance.

## Branding

| Key | Default | Description |
|---|---|---|
| `Branding:ProductName` | `ValkyrieID` | Shown in page titles and in place of a logo. |
| `Branding:Directory` | *(none)* | Directory with branding files, served under `/branding/`. |
| `Branding:Logo` | *(none)* | Logo file name in `Branding:Directory`. |
| `Branding:BackgroundImage` | *(none)* | Background image file name in `Branding:Directory`. |
| `Branding:CustomCss` | *(none)* | Stylesheet file name in `Branding:Directory`, loaded after the built-in styles. |
| `Branding:PrimaryColor` | `#1f4fd1` | Buttons, links, and focus outlines. |
| `Branding:BackgroundColor` | `#eef1f6` | Page background. |
| `Branding:SurfaceColor` | `#ffffff` | Card background. |
| `Branding:TextColor` | `#1a1d23` | Text. |

Colors must be hex (`#1f4fd1`), named (`teal`), `rgb()` or `hsl()` values. File names must be plain names without paths. The server doesn't start if a value is invalid.

The Content Security Policy only allows styles and images from the server itself, so branding files must be in `Branding:Directory`. A custom stylesheet can override the CSS variables `--vid-primary`, `--vid-primary-text`, `--vid-background`, `--vid-surface`, `--vid-text`, `--vid-muted`, `--vid-error`, `--vid-error-background` and `--vid-border`. Keep a contrast ratio of at least 4.5:1 for text (WCAG 2.2 AA).

## Logging

Logs are written to the console as JSON by default (`Logging:Console:FormatterName` = `json`). The `Development` environment uses the human-readable `simple` formatter. Log levels use the standard [`Logging:LogLevel`](https://learn.microsoft.com/aspnet/core/fundamentals/logging/) settings.

## OpenTelemetry

| Key | Default | Description |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | *(none)* | OTLP endpoint. OpenTelemetry is enabled only when this is set. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | *(none)* | Extra headers, e.g. for authentication. |

The other standard `OTEL_*` environment variables are also supported. See [Observability](/operations/observability).
