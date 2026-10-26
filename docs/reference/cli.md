# CLI

The `valkyrieid` binary runs maintenance commands against the configured database without starting the web server. Configuration comes from `appsettings.json` and environment variables, as for the server, but not from command-line arguments. Pending migrations are applied first when `Database:AutoMigrate` is enabled.

```sh
# Docker
docker run --rm -e MasterKey__Current=... -e Issuer__Url=... -v valkyrieid-data:/app/data \
  ghcr.io/valkyrieid/valkyrieid client list

# dotnet publish output
./valkyrieid client list
```

Every change is written to the audit log with the actor `cli`.

## Users

`<user>` is a username or an email address.

| Command | Description |
|---|---|
| `user create <username> [--email <email>] [--name <display name>]` | Create a user. Prompts for the password. |
| `user list` | List users with their state (`enabled`, `locked`, `disabled`), email, and roles. |
| `user enable <user>` / `user disable <user>` | Disabled users can't sign in; disabling also ends their sessions. |

Usernames are 1-64 letters, digits, `.`, `_` or `-`. They can't contain `@`, so a sign-in name with `@` is always treated as an email address. Usernames and email addresses are unique, ignoring case.

### Passwords

Passwords are never accepted as arguments, because arguments end up in shell history and process listings. Commands that need a password show a hidden prompt (asking twice), or read one line from standard input when it is redirected:

```sh
printf '%s\n' "$NEW_PASSWORD" | ./valkyrieid user create alice --email alice@example.com
```

Passwords must satisfy the [password policy](/reference/configuration#passwords).

## Admin recovery

Break-glass commands for when nobody can sign in as an admin.

| Command | Description |
|---|---|
| `admin reset-password <user>` | Set a new password. Also unlocks the account and ends its sessions. |
| `admin unlock <user>` | Clear a lockout. |
| `admin grant-admin <user>` | Give the user the built-in `admin` role. |
| `admin revoke-admin <user>` | Remove the `admin` role. |
| `admin reencrypt [--check]` | Re-encrypt secrets with the current master key. See [Rotate the key](/operations/master-key#rotate-the-key). |

The last enabled admin can't be disabled or lose the `admin` role, so there is always someone who can administer the server.

`admin reencrypt --check` changes nothing. It exits with code `1` if any secret is still encrypted with a previous master key, and `0` otherwise.

## API resources

An API resource is an API that accepts access tokens. Its name is the token audience (`aud`), and it owns one or more scopes. Scope names are unique across all resources, and identity scopes (`openid`, `profile`, `email`, `roles`, `groups`, `offline_access`) are reserved.

| Command | Description |
|---|---|
| `resource create <name> --scope <scope>... [--name <display name>]` | Create a resource with its scopes. |
| `resource list` | List resources and their scopes. |
| `resource add-scope <name> <scope> [--description <text>]` | Add a scope. |
| `resource enable <name>` / `resource disable <name>` | Tokens can't be issued for scopes of a disabled resource. |
| `resource delete <name>` | Delete a resource and its scopes. |

## Clients

| Command | Description |
|---|---|
| `client create <client-id> --scope <scope>... [options]` | Create a client. Confidential clients get their first secret printed. |
| `client list` | List clients with their grants, scopes, redirect URIs, CORS origins, and secrets (ids and expiry only). |
| `client add-secret <client-id> [--description <text>] [--expires-days <days>]` | Add another secret and print it. Not available for public clients. |
| `client remove-secret <client-id> <secret-id>` | Remove a secret. |
| `client add-redirect-uri <client-id> <uri>` / `client remove-redirect-uri <client-id> <uri>` | Manage redirect URIs. A client using `authorization_code` keeps at least one. |
| `client add-post-logout-redirect-uri <client-id> <uri>` / `client remove-post-logout-redirect-uri <client-id> <uri>` | Manage post-logout redirect URIs. |
| `client add-cors-origin <client-id> <origin>` / `client remove-cors-origin <client-id> <origin>` | Manage browser origins allowed to call the token and userinfo endpoints. |
| `client enable <client-id>` / `client disable <client-id>` | Disabled clients can't authenticate or start sign-ins. |
| `client delete <client-id>` | Delete a client and its secrets. |

`client create` options:

| Option | Description |
|---|---|
| `--scope <scope>` | Allowed scope, repeatable. API scopes, or identity scopes (`openid`, `profile`, `email`, `roles`, `groups`) for sign-in. |
| `--grant <grant>` | `client_credentials` (default) or `authorization_code`, repeatable. |
| `--redirect-uri <uri>` | Redirect URI, repeatable. Required for `authorization_code`. |
| `--post-logout-redirect-uri <uri>` | Post-logout redirect URI, repeatable. |
| `--cors-origin <origin>` | Allowed browser origin, repeatable, e.g. `https://app.example.com`. |
| `--public` | Create a public client (SPA, mobile, CLI): no secret, PKCE always required, no `client_credentials`. |
| `--no-pkce` | Let a confidential client use `authorization_code` without PKCE. PKCE is required by default. |
| `--name <display name>` | Display name. |
| `--access-token-lifetime <seconds>` / `--id-token-lifetime <seconds>` | Override the global token lifetimes. |
| `--secret-expires-days <days>` | Expiry of the first secret. |

```sh
# A server-rendered web app (confidential, PKCE)
valkyrieid client create webapp --grant authorization_code \
  --redirect-uri https://app.example.com/signin-oidc \
  --scope openid --scope profile --scope email

# A single-page app (public)
valkyrieid client create spa --public --grant authorization_code \
  --redirect-uri https://spa.example.com/callback --cors-origin https://spa.example.com \
  --scope openid --scope profile --scope orders.read
```

Redirect URIs are matched exactly, character for character. They must be absolute without a fragment and use `https`, `http` on a loopback host, or a private-use scheme in reverse domain notation (`com.example.app:/callback`, RFC 8252). For `http://127.0.0.1` and `http://[::1]` any port is accepted at sign-in, so native and CLI apps can use an ephemeral port. CORS origins are `scheme://host[:port]` without a path or trailing slash.

Secrets are 256-bit random values generated by the server. They are printed **once** and stored only as a SHA-256 hash.

### Rotating a client secret

A client can have several active secrets, so you can rotate without downtime:

1. `client add-secret billing --description "2026-Q4"`
2. Deploy the new secret to the client.
3. `client list` to find the old secret's id, then `client remove-secret billing <secret-id>`.

Alternatively, give each secret an expiry with `--expires-days`.
