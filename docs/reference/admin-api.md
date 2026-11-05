# Admin API

The Admin API is a JSON API under `/api/admin` for managing users, roles, groups, custom claims, clients, API resources, signing keys, sessions, and runtime settings, and for reading the audit log. The admin UI uses it, and you can use it for automation.

The full request and response schemas are in the [OpenAPI document](/openapi/admin-api.json). A server running in the `Development` environment also serves it at `/openapi/v1.json`.

## Authorization

Every request needs an access token in the `Authorization` header:

```http
GET /api/admin/users HTTP/1.1
Host: id.example.com
Authorization: Bearer eyJhbGciOiJSUzI1NiIsImtpZCI6...
```

A request is accepted only if all of the following hold:

1. The token is sent in the `Authorization: Bearer` header. A token in the query string is refused (`400`), and the sign-in cookie alone grants nothing.
2. It is a valid access token from this server (signature, `typ: at+jwt`, not expired, allowing the configured clock skew), and its audience (`aud`) includes `valkyrieid-admin`.
3. It was granted the `valkyrieid.admin` scope.
4. Its `sub` is a user who exists, is enabled, isn't locked out, and has the built-in `admin` role.
5. While [`AdminApi:RequireTwoFactor`](/reference/configuration#admin-api-and-admin-ui) is on (the default), the sign-in behind the token used a second factor: its `amr` claim contains `mfa` ([RFC 8176](https://www.rfc-editor.org/rfc/rfc8176)). Access tokens for the `valkyrieid-admin` audience carry the sign-in's `amr` for this; tokens for other audiences don't.

The user is looked up on **every request**. Removing the `admin` role or disabling the account takes effect immediately, even though access tokens can't be revoked before they expire. Tokens without a user, such as `client_credentials` tokens, are never accepted.

| Response | When |
|---|---|
| `401` with `WWW-Authenticate: Bearer realm="ValkyrieID"` | No token. Every path under `/api/admin` answers this way, including unknown ones. |
| `400` with `error="invalid_request"` | The token is in the URL or the `Authorization` header is malformed. |
| `401` with `error="invalid_token"` | The token is invalid, expired, for another audience, or its user doesn't exist or is disabled. |
| `401` with `error="insufficient_user_authentication"` | The sign-in didn't use a second factor ([RFC 9470](https://www.rfc-editor.org/rfc/rfc9470)). Sign in again with `prompt=login`; users who haven't set up 2FA are asked to. Recorded as `admin_api.access_denied`. |
| `403` with `error="insufficient_scope"`, `scope="valkyrieid.admin"` | The token lacks the scope. |
| `403` | The user doesn't have the `admin` role or is locked out. Recorded in the audit log as `admin_api.access_denied`. |

The API sends no CORS headers, so other websites can't call it from a browser. Responses are never cached (`Cache-Control: no-store`), and carry `Cross-Origin-Resource-Policy: same-origin` and `Cross-Origin-Opener-Policy: same-origin`.

### Getting a token

The server creates two built-in entries at startup:

| Entry | Value |
|---|---|
| API resource (audience) | `valkyrieid-admin`, with the scope `valkyrieid.admin` |
| Admin UI client | `valkyrieid-admin-ui`: public, authorization code flow with PKCE (`S256`), redirect URI `<issuer>/admin/callback`, post-logout redirect URI `<issuer>/admin/`, scopes `openid profile valkyrieid.admin`, no refresh tokens, 10-minute access tokens |

They are kept in this configuration on every start (for example, the redirect URIs follow a changed issuer URL). They can't be changed, disabled, or deleted through the API or the CLI. The names are reserved: no other client, resource, or scope can use them.

For automation, sign in as an admin user with the authorization code flow. You can allow the `valkyrieid.admin` scope on your own client for this; it requires the `authorization_code` grant, because only tokens of admin users are accepted. For unattended scripts on the server itself, use the [CLI](/reference/cli).

## Conventions

- JSON with camelCase property names. Times are ISO 8601 in UTC. Lifetimes are in seconds; `null` means the global setting.
- Entities are addressed by their `id` (a GUID), also clients and API resources, whose `clientId` and `name` can contain characters that aren't safe in a URL path.
- Request bodies are strict: unknown properties, missing required properties, and `null` for required values are rejected with `400`.
- `PUT` replaces the editable fields of an entity. Actions such as `enable` or `unlock` are `POST` requests to a sub-path. Both return the entity as it is afterwards.
- If two admins edit the same entity at once, the last change wins. The audit log records both.

### Errors

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details (`application/problem+json`):

| Status | Meaning |
|---|---|
| `400` | The request is invalid. `errors` maps a field name to messages; the key `""` holds messages that aren't about one field. |
| `404` | The entity doesn't exist. |
| `409` | The change conflicts with the current state: a duplicate name, the last-admin rule, a built-in entity, or a scope still in use. |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "": ["The authorization_code grant needs at least one redirect URI."] }
}
```

### Paging

Lists take `limit` (1-200, default 50), `offset` (default 0), and `q` (a case-insensitive search on names, at most 200 characters), and return:

```json
{ "items": [ ... ], "total": 123 }
```

The audit log uses a cursor instead, because it grows while you page through it; see [below](#audit-log).

## Auditing

Every change is written to the [audit log](#audit-log) with the actor `admin:<sub>`, the admin's immutable subject identifier, and their IP address. Secrets and passwords are never recorded.

## Users

| Request | Description |
|---|---|
| `GET /users` | List users (`q` searches username and email). |
| `POST /users` | Create a user: `userName`, `password`, and optionally `email`, `displayName`, `givenName`, `familyName`. The password must satisfy the [password policy](/reference/configuration#passwords). |
| `GET /users/{id}` | A user with roles, groups, custom claims, lockout, and 2FA state. |
| `PUT /users/{id}` | Set `userName`, `email`, `emailVerified`, `displayName`, `givenName`, and `familyName`. |
| `DELETE /users/{id}` | Delete a user. Their sessions are revoked first, with back-channel logout. |
| `POST /users/{id}/password` | Set a new password (`{ "password": "..." }`). Unlocks the account and revokes all its sessions and refresh tokens. With SMTP, the user gets a notice. |
| `POST /users/{id}/enable`, `/disable` | Disabling revokes the user's sessions and refresh tokens. |
| `POST /users/{id}/unlock` | Clear a lockout. |
| `POST /users/{id}/reset-2fa` | Turn off 2FA and delete the TOTP secret and recovery codes; revokes the user's sessions. |
| `POST /users/{id}/require-2fa`, `/unrequire-2fa` | Require 2FA for this user regardless of the global setting. |
| `POST /users/{id}/send-verification` | Email a verification link (`202`). `409` without SMTP, without an address, or if it's already verified. |
| `PUT /users/{id}/roles/{roleId}`, `DELETE ...` | Assign or remove a role. The user's sessions must sign in again. |
| `PUT /users/{id}/groups/{groupId}`, `DELETE ...` | Add to or remove from a group. |
| `POST /users/{id}/claims`, `DELETE /users/{id}/claims/{claimId}` | Add (`{ "type": "department", "value": "finance" }`) or remove a custom claim. |
| `GET /users/{id}/sessions` | Active sessions, with the clients that signed in with each. |
| `DELETE /users/{id}/sessions/{sid}`, `DELETE /users/{id}/sessions` | Revoke one or all sessions (force sign-out): back-channel logout, and the sessions' refresh tokens are revoked. |

Changing the email address marks it unverified and voids pending email links, such as password resets sent to the old address; the old address gets a notice. A changed address can't be marked verified in the same request.

The last enabled admin can't be disabled, deleted, or lose the `admin` role.

### Custom claims

Custom claims are emitted with the `profile` scope. Claim types are 1-100 characters without spaces or quotes; values are up to 1000 characters; a user has at most 100. Protocol and standard claims are reserved and can't be set, so a custom claim can never replace them: `iss`, `sub`, `aud`, `exp`, `nbf`, `iat`, `jti`, `auth_time`, `nonce`, `acr`, `amr`, `azp`, `sid`, `at_hash`, `c_hash`, `s_hash`, `scope`, `client_id`, `cnf`, `typ`, the standard profile, email, phone, and address claims, `roles`, and `groups`.

## Roles and groups

| Request | Description |
|---|---|
| `GET /roles`, `POST /roles` | List or create roles (`{ "name": "editor", "description": "..." }`). |
| `GET /roles/{id}`, `PUT /roles/{id}`, `DELETE /roles/{id}` | Read, rename, or delete a role. Renaming or deleting a role signs out its members' sessions. |
| `GET /groups`, `POST /groups` | List or create groups. |
| `GET /groups/{id}`, `PUT /groups/{id}`, `DELETE /groups/{id}` | Read, rename, or delete a group. |

Names are 1-100 characters and unique, ignoring case. They are emitted in the `roles` and `groups` claims. The built-in `admin` role can't be renamed or deleted.

## Clients

| Request | Description |
|---|---|
| `GET /clients` | List clients (`q` searches the client id and display name). Secrets are listed by id and expiry only. |
| `POST /clients` | Create a client. Returns the client and, for a confidential client, its first secret. |
| `GET /clients/{id}` | A client. |
| `PUT /clients/{id}` | Replace the client's configuration. |
| `DELETE /clients/{id}` | Delete a client with its secrets and refresh tokens. |
| `POST /clients/{id}/enable`, `/disable` | Disabled clients can't authenticate or start sign-ins; disabling revokes their refresh tokens. |
| `POST /clients/{id}/secrets` | Add a secret (`{ "description": "2026-Q4", "expiresDays": 90 }`, both optional). |
| `DELETE /clients/{id}/secrets/{secretId}` | Remove a secret. |

```json
{
  "clientId": "webapp",
  "type": "confidential",
  "grantTypes": ["authorization_code", "refresh_token"],
  "scopes": ["openid", "profile", "offline_access"],
  "redirectUris": ["https://app.example.com/signin-oidc"],
  "postLogoutRedirectUris": ["https://app.example.com/signout-callback-oidc"],
  "corsOrigins": [],
  "backChannelLogoutUri": null,
  "requirePkce": true,
  "allowClientSecretPost": false,
  "accessTokenLifetime": null,
  "idTokenLifetime": null,
  "refreshTokenLifetime": null,
  "refreshTokenAbsoluteLifetime": null,
  "displayName": "Web app",
  "secretExpiresDays": 90
}
```

`clientId` and `type` (`confidential` or `public`) are set at creation and can't be changed. `PUT` takes the same fields without `clientId`, `type`, and `secretExpiresDays`. The configuration is validated as a whole with the same rules as the [CLI](/reference/cli#clients); for example, public clients always use PKCE and can't use `client_credentials`, and redirect URIs must be `https` (or `http` on a loopback host, or a private-use scheme).

If a change removes a grant type or a scope, the client's refresh tokens are revoked, so tokens issued under the old configuration can't keep access it no longer has.

Secrets are returned **once**, in the response that creates them, and stored only as a SHA-256 hash:

```json
{ "id": "0199...", "secret": "kq3v...", "expiresAt": "2027-01-30T10:00:00+00:00" }
```

## API resources

| Request | Description |
|---|---|
| `GET /resources`, `POST /resources` | List or create resources (`{ "name": "orders-api", "displayName": "Orders", "scopes": [{ "name": "orders.read", "description": "..." }] }`). |
| `GET /resources/{id}`, `PUT /resources/{id}`, `DELETE /resources/{id}` | Read, update (`displayName`, `allowClientSecretPost`), or delete a resource. |
| `POST /resources/{id}/enable`, `/disable` | Tokens can't be issued for scopes of a disabled resource, and it can't call introspection. |
| `POST /resources/{id}/scopes` | Add a scope. |
| `PUT /resources/{id}/scopes/{scopeId}`, `DELETE ...` | Change a scope's description, or remove it. |
| `POST /resources/{id}/secrets`, `DELETE /resources/{id}/secrets/{secretId}` | Add or remove a secret for the [introspection endpoint](/reference/endpoints#introspection-endpoint). |

Scope names can't be changed. A scope, or a resource with its scopes, can't be removed while a client is allowed to request it (`409` names the clients); remove the scope from those clients first.

## Signing keys

| Request | Description |
|---|---|
| `GET /signing-keys` | Keys with their `status`: `pending` (published in JWKS, not signing yet), `active`, or `retired` (still published for tokens it signed). |
| `POST /signing-keys/rotate` | Start a rotation now. |

A manual rotation follows the same [lifecycle](/operations/key-rotation#lifecycle) as a scheduled one: the new key is published at once and signs tokens after `SigningKeys:PublishLead`. While a key is pending, another rotation is refused with `409`. Private keys are never returned.

## Settings

The [runtime settings](/reference/configuration#runtime-settings), grouped in sections: `tokens`, `session`, `passwordPolicy`, `lockout`, `twoFactor`, `adminApi`, `email`, `smtp`, `branding`, and `auditLog`.

| Request | Description |
|---|---|
| `GET /settings` | All sections. |
| `GET /settings/{section}` | One section. |
| `PATCH /settings/{section}` | Change some settings of a section; the others stay as they are. Returns the section. |
| `POST /settings/smtp/test` | Send a test email to your own address right away, with the current settings. |

```json
{
  "name": "lockout",
  "settings": [
    {
      "name": "maxFailedAttempts",
      "key": "Lockout:MaxFailedAttempts",
      "kind": "integer",
      "value": 3,
      "defaultValue": 5,
      "locked": true,
      "source": "configuration",
      "isSet": null,
      "options": null
    }
  ]
}
```

- `kind` is `duration` (whole seconds), `integer`, `boolean`, `string`, `enum` or `file` (one of `options`), or `secret`.
- `source` is `default`, `database`, or `configuration`. Settings from configuration are `locked`: changing one answers `409`.
- Secrets are never returned: `value` is always `null`, and `isSet` says whether one is stored.

A `PATCH` body maps setting names to values; `null` resets a setting to its default (for a secret: removes it). An empty string also resets text settings and secrets.

```json
{ "maxFailedAttempts": 10, "duration": 1800 }
```

The settings are validated together, with the configuration, before anything is saved; invalid values answer `400`. In addition:

- `email.verificationAtSignIn` can be `Prompt` or `Require` only while SMTP is configured.
- `branding` files must be files of the right type in `Branding:Directory`.
- Turning `adminApi.requireTwoFactor` on answers `409` unless your own sign-in used a second factor, so you can't lock yourself out.

Changes are recorded as `settings.updated`, with the old and new values of each setting (secrets as `(secret)`).

The test email answers `{ "success": true, "result": "sent", "to": "admin@example.com" }`, or `success: false` with a short reason such as `authentication_failed`, `network_error`, or `smtp_550`. Details of the failure are never returned. It answers `409` without SMTP or when your account has no email address, and is rate-limited like the [email forms](/reference/configuration#rate-limits).

## Audit log

`GET /audit` returns entries newest first:

```json
{
  "items": [
    {
      "id": "0199...",
      "timestamp": "2026-11-01T10:00:00+00:00",
      "eventType": "user.role_added",
      "success": true,
      "subjectId": "0199...",
      "actor": "admin:0199...",
      "clientId": null,
      "ipAddress": "203.0.113.7",
      "details": { "role": "editor" }
    }
  ],
  "next": "NjM4..."
}
```

Pass `next` as `cursor` to get the following page; it is `null` on the last page. Filters:

| Parameter | Description |
|---|---|
| `limit` | 1-200, default 50. |
| `type` | An event type, e.g. `user.created`, or a prefix ending in `.`, e.g. `user.`. |
| `subject` | The user (`sub`) the event concerns. |
| `actor` | Who acted, e.g. `cli`, `system`, or `admin:<sub>`. |
| `clientId` | The client involved. |
| `success` | `true` or `false`. |
| `from`, `to` | Time range (`from` inclusive, `to` exclusive). |
