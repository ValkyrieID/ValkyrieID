# CLI

The `valkyrieid` binary runs maintenance commands against the configured database without starting the web server. Configuration comes from `appsettings.json` and environment variables, as for the server, but not from command-line arguments. Pending migrations are applied first when `Database:AutoMigrate` is enabled.

```sh
# Docker (a running container already has the configuration)
docker exec -it valkyrieid dotnet /app/valkyrieid.dll client list
# or a one-off container on the same volume
docker run --rm -e MasterKey__Current=... -e Issuer__Url=... -v valkyrieid-data:/data \
  ghcr.io/valkyrieid/valkyrieid client list

# dotnet publish output
./valkyrieid client list
```

Every change is written to the audit log with the actor `cli`. The same changes can be made over HTTP with the [Admin API](/reference/admin-api); both share the same validation.

## Users

`<user>` is a username or an email address.

| Command | Description |
|---|---|
| `user create <username> [--email <email>] [--name <display name>]` | Create a user. Prompts for the password. |
| `user list` | List users with their state (`enabled`, `locked`, `disabled`), email, roles, and 2FA state (`on`, `required`, `off`). |
| `user enable <user>` / `user disable <user>` | Disabled users can't sign in; disabling also revokes their sessions. |
| `user require-2fa <user>` / `user unrequire-2fa <user>` | Require [two-factor authentication](/guide/two-factor) for this user, regardless of `TwoFactor:Required`. Applies from their next sign-in. |

Usernames are 1-64 letters, digits, `.`, `_` or `-`. They can't contain `@`, so a sign-in name with `@` is always treated as an email address. Usernames and email addresses are unique, ignoring case.

### Passwords

Passwords are never accepted as arguments, because arguments end up in shell history and process listings. Commands that need a password show a hidden prompt (asking twice), or read one line from standard input when it is redirected:

```sh
printf '%s\n' "$NEW_PASSWORD" | ./valkyrieid user create alice --email alice@example.com
```

Passwords must satisfy the [password policy](/reference/configuration#passwords).

## Sessions

| Command | Description |
|---|---|
| `session list --user <user>` | List the user's active sessions: session id (`sid`), sign-in time, last activity, expiry, IP address, user agent, and the clients that signed in with it. |
| `session revoke <session-id>` | Revoke one session. |
| `session revoke --user <user>` | Revoke all of the user's active sessions. |

Revoking a session signs the user out immediately, revokes the refresh tokens issued from it, and notifies its clients by [back-channel logout](/reference/endpoints#back-channel-logout). Disabling a user or resetting their password revokes their sessions the same way.

A running server delivers the notifications within `BackChannelLogout:PollInterval` (1 minute by default), because the CLI can't wake it directly.

## Admin recovery

Break-glass commands for when nobody can sign in as an admin. If an admin can't sign in because 2FA is required and they lost their authenticator, use `admin reset-2fa`; they set it up again at their next sign-in.

| Command | Description |
|---|---|
| `admin reset-password <user>` | Set a new password. Also unlocks the account and revokes its sessions. With SMTP, the user's address gets a notice. |
| `admin unlock <user>` | Clear a lockout. |
| `admin reset-2fa <user>` | Turn off two-factor authentication, e.g. after a lost phone: deletes the TOTP secret and recovery codes and revokes the user's sessions. If 2FA is required, the user sets it up again at their next sign-in. |
| `admin grant-admin <user>` | Give the user the built-in `admin` role. |
| `admin revoke-admin <user>` | Remove the `admin` role. |
| `admin reencrypt [--check]` | Re-encrypt secrets with the current master key. See [Rotate the key](/operations/master-key#rotate-the-key). |
| `admin send-test-email <address>` | Send a test email right away, to check the [SMTP settings](/reference/configuration#smtp). Prints the SMTP error if it fails. |

The last enabled admin can't be disabled or lose the `admin` role, so there is always someone who can administer the server.

`admin reencrypt --check` changes nothing. It exits with code `1` if any secret is still encrypted with a previous master key, and `0` otherwise.

## Settings

Show and change the [runtime settings](/reference/configuration#runtime-settings) stored in the database. Keys are configuration keys; sections are the first part of the key (e.g. `Tokens`) or the Admin API's section name (e.g. `passwordPolicy`).

| Command | Description |
|---|---|
| `settings show [<section>]` | List the runtime settings with their effective value and where it comes from: `default`, `database`, or `configuration`. Secrets show only `(set)` or `(not set)`. |
| `settings set <key> <value>` | Store a value, written as in configuration: e.g. `settings set Tokens:AccessTokenLifetime 00:15:00`, `settings set Smtp:Security Tls`. |
| `settings set Smtp:Password` | Store the SMTP password, read like a [password](#passwords) (never as an argument). It is encrypted with the master key. |
| `settings reset <key\|section>` | Remove stored values, so the defaults apply. |

Settings set in configuration can't be changed here. Values are validated like in the Admin API. Running servers pick up the change within `Settings:ReloadInterval`.

If stored settings keep the server from starting (for example after a configuration change made them invalid), fix them with `settings set` or `settings reset`: the CLI doesn't validate them on startup.

## API resources

An API resource is an API that accepts access tokens. Its name is the token audience (`aud`), and it owns one or more scopes. Scope names are unique across all resources, and identity scopes (`openid`, `profile`, `email`, `roles`, `groups`, `offline_access`) are reserved. The resource name `valkyrieid-admin` and the scope `valkyrieid.admin` are reserved for the built-in [Admin API](/reference/admin-api#getting-a-token) resource, which can't be changed.

| Command | Description |
|---|---|
| `resource create <name> --scope <scope>... [--name <display name>]` | Create a resource with its scopes. |
| `resource list` | List resources with their scopes and secrets (ids and expiry only). |
| `resource add-scope <name> <scope> [--description <text>]` | Add a scope. |
| `resource add-secret <name> [--description <text>] [--expires-days <days>]` | Add a secret and print it. The API uses it to call the [introspection endpoint](/reference/endpoints#introspection-endpoint). |
| `resource remove-secret <name> <secret-id>` | Remove a secret. |
| `resource enable <name>` / `resource disable <name>` | Tokens can't be issued for scopes of a disabled resource, and it can't call introspection. |
| `resource delete <name>` | Delete a resource with its scopes and secrets. Refused while a client is allowed one of its scopes. |
| `resource enable-secret-post <name>` / `resource disable-secret-post <name>` | Allow or refuse sending the resource secret as form parameters when calling the introspection endpoint. Off by default. |

## Clients

| Command | Description |
|---|---|
| `client create <client-id> --scope <scope>... [options]` | Create a client. Confidential clients get their first secret printed. |
| `client list` | List clients with their grants, scopes, redirect URIs, back-channel logout URI, CORS origins, and secrets (ids and expiry only). |
| `client add-secret <client-id> [--description <text>] [--expires-days <days>]` | Add another secret and print it. Not available for public clients. |
| `client remove-secret <client-id> <secret-id>` | Remove a secret. |
| `client add-redirect-uri <client-id> <uri>` / `client remove-redirect-uri <client-id> <uri>` | Manage redirect URIs. A client using `authorization_code` keeps at least one. |
| `client add-post-logout-redirect-uri <client-id> <uri>` / `client remove-post-logout-redirect-uri <client-id> <uri>` | Manage post-logout redirect URIs. |
| `client add-cors-origin <client-id> <origin>` / `client remove-cors-origin <client-id> <origin>` | Manage browser origins allowed to call the token, userinfo, and revocation endpoints. |
| `client set-backchannel-logout-uri <client-id> <uri>` / `client clear-backchannel-logout-uri <client-id>` | Set or remove the [back-channel logout](/reference/endpoints#back-channel-logout) URI. |
| `client enable <client-id>` / `client disable <client-id>` | Disabled clients can't authenticate or start sign-ins. Disabling also revokes the client's refresh tokens. |
| `client enable-secret-post <client-id>` / `client disable-secret-post <client-id>` | Allow or refuse `client_secret_post` for a confidential client. |
| `client delete <client-id>` | Delete a client with its secrets and refresh tokens. |

`client create` options:

| Option | Description |
|---|---|
| `--scope <scope>` | Allowed scope, repeatable. API scopes, or identity scopes (`openid`, `profile`, `email`, `roles`, `groups`, `offline_access`) for sign-in. |
| `--grant <grant>` | `client_credentials` (default), `authorization_code`, or `refresh_token`, repeatable. `refresh_token` needs `authorization_code`, and refresh tokens are only issued if the client is also allowed `--scope offline_access`. |
| `--redirect-uri <uri>` | Redirect URI, repeatable. Required for `authorization_code`. |
| `--post-logout-redirect-uri <uri>` | Post-logout redirect URI, repeatable. Used by the [end session endpoint](/reference/endpoints#end-session-endpoint), which matches it exactly. |
| `--backchannel-logout-uri <uri>` | Where [logout tokens](/reference/endpoints#back-channel-logout) are posted when a session ends. Needs `authorization_code`. Absolute `https` without a fragment; `http` only on a loopback host for confidential clients. |
| `--cors-origin <origin>` | Allowed browser origin, repeatable, e.g. `https://app.example.com`. |
| `--public` | Create a public client (SPA, mobile, CLI): no secret, PKCE always required, no `client_credentials`. |
| `--no-pkce` | Let a confidential client use `authorization_code` without PKCE in OpenID Connect requests (with `openid`, where `nonce` protects against code injection); requests without `openid` always need PKCE. PKCE is required by default, and is recommended for confidential clients too (RFC 9700 §2.1.1). |
| `--allow-secret-post` | Let a confidential client send its secret as form parameters (`client_secret_post`). Off by default: RFC 6749 §2.3.1 recommends HTTP Basic (`client_secret_basic`). |
| `--name <display name>` | Display name. |
| `--access-token-lifetime <seconds>` / `--id-token-lifetime <seconds>` | Override the global token lifetimes. |
| `--refresh-token-lifetime <seconds>` / `--refresh-token-absolute-lifetime <seconds>` | Override the global sliding and absolute refresh token lifetimes. Only with the `refresh_token` grant. |
| `--secret-expires-days <days>` | Expiry of the first secret. |

```sh
# A server-rendered web app (confidential, PKCE) that keeps users signed in with refresh tokens
valkyrieid client create webapp --grant authorization_code --grant refresh_token \
  --redirect-uri https://app.example.com/signin-oidc \
  --scope openid --scope profile --scope email --scope offline_access

# A single-page app (public)
valkyrieid client create spa --public --grant authorization_code \
  --redirect-uri https://spa.example.com/callback --cors-origin https://spa.example.com \
  --scope openid --scope profile --scope orders.read
```

Redirect URIs are matched exactly, character for character. They must be absolute without a fragment and use `https`, `http` on a loopback host, or a private-use scheme in reverse domain notation (`com.example.app:/callback`, RFC 8252). For `http://127.0.0.1` and `http://[::1]` any port is accepted at sign-in, so native and CLI apps can use an ephemeral port; this exception doesn't apply to post-logout redirect URIs. CORS origins are `scheme://host[:port]` without a path or trailing slash.

Secrets are 256-bit random values generated by the server. They are printed **once** and stored only as a SHA-256 hash.

Grants, scopes, and lifetimes are set when a client is created. To change them, use the [Admin API](/reference/admin-api#clients), or recreate the client.

The client id `valkyrieid-admin-ui` is reserved for the built-in admin UI client, which `client list` marks as `built-in` and which can't be changed. Post-logout redirect URIs and back-channel logout URIs need the `authorization_code` grant. Client ids that are GUIDs are refused, so a client can't be mistaken for a user, whose `sub` is a GUID (RFC 9700 4.15).

### Rotating a client secret

A client can have several active secrets, so you can rotate without downtime:

1. `client add-secret billing --description "2026-Q4"`
2. Deploy the new secret to the client.
3. `client list` to find the old secret's id, then `client remove-secret billing <secret-id>`.

Alternatively, give each secret an expiry with `--expires-days`.

## Health check

| Command | Description |
|---|---|
| `healthcheck [--ready\|--live] [--url <url>] [--timeout <seconds>]` | Call `/health/ready` (default) or `/health/live` of the server running on this machine over loopback; exit with `0` when it answers `200`, `1` otherwise. |

Unlike the other commands, `healthcheck` doesn't open the database or need the master key; it only reads the listening address (`ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_URLS`, or `Kestrel:Endpoints`) and `ReverseProxy:PathBase`. It is the [container health check](/guide/docker#health-check).
