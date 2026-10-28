# Endpoints

All URLs are relative to the configured [issuer](/reference/configuration#issuer).

| Endpoint | Method | Spec |
|---|---|---|
| `/.well-known/openid-configuration` | `GET` | OpenID Connect Discovery 1.0, RFC 8414 |
| `/.well-known/jwks.json` | `GET` | RFC 7517 |
| `/connect/token` | `POST` | RFC 6749 §3.2 |
| `/connect/authorize` | `GET`, `POST` | RFC 6749 §3.1, OpenID Connect Core 1.0 §3.1.2 |
| `/connect/userinfo` | `GET`, `POST` | OpenID Connect Core 1.0 §5.3 |
| `/connect/revocation` | `POST` | RFC 7009 |
| `/connect/introspect` | `POST` | RFC 7662 |

The end session endpoint arrives in a later release. Discovery only advertises what is implemented.

## Pages

Server-rendered pages for people, not clients. They work without JavaScript, and forms are protected with anti-forgery tokens.

| Page | Description |
|---|---|
| `/setup` | First-run setup wizard. Only available while no user exists; returns `404` afterwards. See [First run](/guide/first-run). |
| `/account/login` | Sign in with a username or email address and password. Accepts a local `returnUrl`; other URLs are ignored. `prompt=login` shows the form even when a session exists; `login_hint` pre-fills the username. |
| `/account/logout` | Asks for confirmation, then ends the session. |
| `/account` | Shows who is signed in. |

Every response carries these headers:

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'none'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'` |
| `X-Frame-Options` | `DENY` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `no-referrer` |

`Strict-Transport-Security` is added to HTTPS responses outside the `Development` environment.

## Discovery

Returns the issuer and endpoint URLs, plus:

| Field | Value |
|---|---|
| `scopes_supported` | `openid`, `profile`, `email`, `roles`, `groups`, `offline_access` |
| `response_types_supported` | `code` |
| `response_modes_supported` | `query`, `form_post` |
| `grant_types_supported` | `authorization_code`, `client_credentials`, `refresh_token` |
| `token_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post`, `none` |
| `revocation_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post`, `none` |
| `introspection_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post` |
| `code_challenge_methods_supported` | `S256` |
| `id_token_signing_alg_values_supported` | `RS256` |
| `prompt_values_supported` | `none`, `login`, `consent`, `select_account` |
| `authorization_response_iss_parameter_supported` | `true` |
| `request_parameter_supported`, `request_uri_parameter_supported`, `claims_parameter_supported` | `false` |

## JWKS

Returns the public RSA keys (`kty`, `use`, `alg`, `kid`, `n`, `e`). This includes keys that are not used yet and retired keys whose tokens may still be valid. See [Signing key rotation](/operations/key-rotation).

## Authorization endpoint

Starts a sign-in with the authorization code flow. Parameters come from the query string (`GET`) or a form body (`POST`). Parameters must not be repeated, and a parameter sent without a value counts as omitted.

| Parameter | Required | Description |
|---|---|---|
| `response_type` | yes | `code` |
| `client_id` | yes | A registered, enabled client allowed to use `authorization_code`. |
| `redirect_uri` | yes | Must match a registered redirect URI exactly (any port for `http://127.0.0.1` and `http://[::1]`). |
| `scope` | yes | Space-separated. Include `openid` for OpenID Connect; without it the request is plain OAuth 2.0 and returns only an access token. In OpenID Connect requests, scopes the server doesn't know (e.g. `address`) are ignored; scopes that exist but aren't allowed for the client are rejected. `offline_access` requests a refresh token; see [Refresh tokens](#refresh-token-grant). |
| `code_challenge`, `code_challenge_method` | public clients, and confidential clients unless created with `--no-pkce` | PKCE (RFC 7636). Only `S256`. |
| `state` | recommended | Returned unchanged. |
| `nonce` | recommended | Copied into the ID token. |
| `response_mode` | no | `query` (default) or `form_post`. |
| `prompt` | no | `none`: never show the login page, return `login_required` instead. `login` or `select_account`: always ask the user to sign in again. `consent`: accepted; there are no consent screens. `none` can't be combined with other values. |
| `max_age` | no | Seconds since the last sign-in after which the user must sign in again. |
| `login_hint` | no | Pre-fills the username on the login page. |
| `display`, `ui_locales`, `claims_locales`, `acr_values`, `id_token_hint` | no | Accepted and ignored. |

If the user has no session, the browser is sent to `/account/login` and comes back to the authorization request after signing in. A new sign-in replaces the previous session.

The response goes to the redirect URI, with `code`, `state`, and `iss` (RFC 9207). With `response_mode=form_post` the response is an HTML page that posts the parameters to the redirect URI. A script with a per-response CSP nonce submits it automatically; without JavaScript the user presses **Continue**.

```
HTTP/1.1 302 Found
Location: https://app.example.com/callback?code=SplxlOBeZQQYbYS6WxSbIA&state=af0ifjsldkj&iss=https%3A%2F%2Fid.example.com
```

The session cookie is `SameSite=Lax`. It is sent on top-level `GET` navigations, so `GET` authorization requests use the existing session. Cross-site `POST` requests don't carry it, so the user may be asked to sign in.

### Authorization errors

If `client_id` or `redirect_uri` is missing, unknown, disabled, or doesn't match, ValkyrieID shows an error page and **does not redirect**. All other errors are sent to the redirect URI as `error`, `error_description`, `state`, and `iss`:

| Error | Cause |
|---|---|
| `invalid_request` | Missing or repeated parameter, missing PKCE, a method other than `S256`, malformed `code_challenge`, invalid `prompt` or `max_age`, unsupported `response_mode` |
| `unsupported_response_type` | `response_type` other than `code` |
| `unauthorized_client` | The client may not use `authorization_code` |
| `invalid_scope` | `scope` is missing or malformed; a scope isn't allowed for the client; an identity scope without `openid`; or, in plain OAuth requests, an unknown scope |
| `login_required` | `prompt=none` and the user must sign in |
| `request_not_supported`, `request_uri_not_supported`, `registration_not_supported` | Request objects and registration aren't supported |

## Token endpoint

Requests must be `POST` with `application/x-www-form-urlencoded` parameters. Parameters must not be repeated. Responses are JSON with `Cache-Control: no-store`.

### Client authentication

Confidential clients authenticate with one of:

- **`client_secret_basic`**: HTTP Basic, with the client id and secret each form-urlencoded first (RFC 6749 §2.3.1).
- **`client_secret_post`**: `client_id` and `client_secret` form parameters.

Using both in the same request is rejected.

Public clients send only `client_id` in the body (`none`). A public client that sends a secret is rejected.

### `client_credentials` grant

| Parameter | Required | Description |
|---|---|---|
| `grant_type` | yes | `client_credentials` |
| `scope` | no | Space-separated API scopes. If omitted, all API scopes the client may use are granted. |

```sh
curl -u billing:$SECRET https://id.example.com/connect/token \
  -d grant_type=client_credentials -d scope=orders.read
```

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsImtpZCI6...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "orders.read"
}
```

The access token is a JWT following RFC 9068:

| Claim | Value |
|---|---|
| header `typ` | `at+jwt` |
| `iss` | The issuer |
| `aud` | The API resources that own the granted scopes |
| `sub`, `client_id` | The client id |
| `scope` | The granted scopes |
| `iat`, `nbf`, `exp`, `jti` | Issue time, not-before, expiry, unique id |

Access tokens can't be revoked before they expire; keep their lifetime short.

### `authorization_code` grant

| Parameter | Required | Description |
|---|---|---|
| `grant_type` | yes | `authorization_code` |
| `code` | yes | The code from the authorization response. |
| `redirect_uri` | yes | Identical to the one in the authorization request. |
| `code_verifier` | if PKCE was used | The PKCE verifier. Must not be sent if the authorization request had no `code_challenge`. |
| `client_id` | public clients | Confidential clients authenticate as above. |

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsImtpZCI6...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile",
  "id_token": "eyJhbGciOiJSUzI1NiIsImtpZCI6..."
}
```

A code can be redeemed once, within 60 seconds, by the client it was issued to. It is consumed by the first attempt, even a failed one. It also stops working when the user's session ends.

The access token's `sub` is the user's id and its `aud` is the API resources that own the granted API scopes, or the client id if no API scope was granted. `id_token` is included when `openid` was granted, and `refresh_token` when `offline_access` was granted.

### `refresh_token` grant

A client gets a refresh token when all of these hold:

- the client is allowed the `refresh_token` grant and the `offline_access` scope;
- the authorization request was an OpenID Connect request (`openid`) that included `offline_access`.

Otherwise `offline_access` is silently dropped from the granted scope. ValkyrieID has no consent screen: an administrator allowing a client `offline_access` is the consent OpenID Connect Core §11 requires. Public clients get refresh tokens too; rotation protects them (RFC 9700 §4.14.2).

| Parameter | Required | Description |
|---|---|---|
| `grant_type` | yes | `refresh_token` |
| `refresh_token` | yes | The current refresh token. |
| `scope` | no | A subset of the originally granted scope, to get a narrower access token. |
| `client_id` | public clients | Confidential clients authenticate as above. |

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsImtpZCI6...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid offline_access",
  "id_token": "eyJhbGciOiJSUzI1NiIsImtpZCI6...",
  "refresh_token": "tGzv3JOkF0XG5Qx2TlKWIA..."
}
```

- **Rotation.** Every use returns a new refresh token and invalidates the one sent. The new token keeps the original scope, even when `scope` narrowed the access token.
- **Reuse detection.** Sending a refresh token that was already used revokes every token descended from the same sign-in (the token family). The client has to sign the user in again. Clients must store the newest token before using it, and must not retry a refresh with an old token.
- **Lifetimes.** A token expires when unused for 14 days (sliding). A family ends 30 days after the original sign-in (absolute), however often it is refreshed. Both are [configurable](/reference/configuration#tokens) globally and per client.
- **ID tokens.** A refreshed ID token keeps `sub`, `aud`, `auth_time`, `amr`, and `sid` from the original sign-in and has no `nonce` (OpenID Connect Core §12.2). User claims reflect the user's current data.
- **Storage.** Refresh tokens are opaque, random 256-bit values. Only their SHA-256 hash is stored.

Refresh tokens are revoked when:

| Event | Effect |
|---|---|
| A used token is sent again | Its family is revoked. |
| An authorization code is redeemed twice | Refresh tokens issued from that code are revoked (RFC 6749 §4.1.2). |
| The client calls the [revocation endpoint](#revocation-endpoint) | The token's family is revoked. |
| The user's password is reset, or the user is disabled | All of the user's refresh tokens are revoked. |
| The client is disabled | All of the client's refresh tokens are revoked. Deleting the client deletes them. |

Signing out does **not** revoke refresh tokens: `offline_access` means access while the user is not signed in (OpenID Connect Core §11, Back-Channel Logout §2.7). Refreshing still requires the user and the client to be enabled and the scopes to still be allowed for the client.

### ID tokens

ID tokens are JWTs signed with RS256:

| Claim | Value |
|---|---|
| `iss`, `sub`, `aud` | The issuer, the user's immutable id, the client id |
| `iat`, `exp` | Issue time and expiry (default 5 minutes) |
| `auth_time`, `amr` | When and how the user signed in (`pwd`) |
| `sid` | The session id |
| `nonce` | From the authorization request, if sent |
| `at_hash` | Hash of the access token (OpenID Connect Core A3.1.3.6) |

User claims for the granted scopes are included in the ID token and returned by userinfo:

| Scope | Claims |
|---|---|
| `profile` | `name`, `given_name`, `family_name`, `preferred_username`, `updated_at`, and the user's custom claims |
| `email` | `email`, `email_verified` |
| `roles` | `roles` (array) |
| `groups` | `groups` (array) |

Empty values are left out. Custom claims can't use the names of protocol or standard claims; such claims are skipped.

### Errors

Errors follow RFC 6749 §5.2:

| Error | Status | Cause |
|---|---|---|
| `invalid_request` | 400 | Wrong content type, repeated or missing parameters, or two authentication methods |
| `invalid_client` | 401 | Client authentication failed (unknown or disabled client, wrong or expired secret, missing secret, public client with a secret) |
| `invalid_grant` | 400 | The code is unknown, expired, already used, issued to another client or redirect URI, the PKCE verifier doesn't match, or the session has ended. The refresh token is unknown, expired, revoked, already used, or issued to another client, or the user is disabled |
| `unauthorized_client` | 400 | The client may not use this grant type |
| `unsupported_grant_type` | 400 | The grant type isn't supported |
| `invalid_scope` | 400 | A scope is malformed, unknown, disabled, an identity scope, or not allowed for the client; or a refresh request asks for more than was granted |

Requests over the [rate limit](/reference/configuration#rate-limits) get `429 Too Many Requests` with a `Retry-After` header.

All client authentication failures return the same response, so callers can't tell which clients exist.

## Userinfo endpoint

Returns the user's claims for the scopes of the access token. Send the token in the `Authorization: Bearer` header, or as `access_token` in a form-encoded `POST` body (RFC 6750). Tokens in the query string are ignored.

The token must be a valid, unexpired access token from this issuer (with the configured clock skew) that includes the `openid` scope. Its audience isn't checked. The response always contains `sub`, plus the claims listed under [ID tokens](#id-tokens). It reflects the user's current data.

| Status | `WWW-Authenticate` | Cause |
|---|---|---|
| 401 | `Bearer realm="ValkyrieID"` | No token |
| 401 | `error="invalid_token"` | Invalid, expired, or not an access token; or the user is disabled or deleted |
| 403 | `error="insufficient_scope"` | The token doesn't include `openid` (e.g. a `client_credentials` token) |
| 400 | `error="invalid_request"` | Malformed header, or the token was sent twice |

## Revocation endpoint

Revokes a refresh token (RFC 7009). Send a form-encoded `POST` with `token` and optionally `token_type_hint`. The client authenticates as at the [token endpoint](#client-authentication); public clients send `client_id`.

```sh
curl -u web:$SECRET https://id.example.com/connect/revocation \
  -d token=$REFRESH_TOKEN -d token_type_hint=refresh_token
```

| Status | Cause |
|---|---|
| 200 | The token's family was revoked, or the token was unknown, expired, or already revoked (RFC 7009 §2.2) |
| 400 `unauthorized_client` | The refresh token was issued to another client (RFC 7009 §2.1). It stays valid. |
| 400 `unsupported_token_type` | The token is one of this client's access tokens. Access tokens can't be revoked; they expire on their own. |
| 400 `invalid_request` | Wrong content type, repeated parameters, or no `token` |
| 401 `invalid_client` | Client authentication failed |

`token_type_hint` is only a hint; both token types are checked.

## Introspection endpoint

Reports whether a token is active (RFC 7662). Send a form-encoded `POST` with `token` and optionally `token_type_hint`. The caller authenticates with `client_secret_basic` or `client_secret_post` as either:

- a **confidential client**, or
- an **API resource**, with a secret created by `resource add-secret` (see [CLI](/reference/cli#api-resources)).

Public clients can't introspect.

```sh
curl -u orders-api:$RESOURCE_SECRET https://id.example.com/connect/introspect -d token=$ACCESS_TOKEN
```

```json
{
  "active": true,
  "scope": "orders.read",
  "client_id": "billing",
  "token_type": "Bearer",
  "exp": 1767225600,
  "iat": 1767223800,
  "nbf": 1767223800,
  "sub": "billing",
  "aud": ["orders-api"],
  "iss": "https://id.example.com",
  "jti": "Qp3x..."
}
```

A caller only learns about:

- access tokens issued to it (its `client_id`) or whose `aud` includes it (an API resource);
- refresh tokens issued to it (clients only).

Anything else, including unknown, expired, revoked, and rotated tokens, returns only `{"active": false}`.

- **Refresh tokens** are checked against the database, so revocation, rotation, and reuse detection show immediately.
- **Access tokens** are checked statelessly: signature, issuer, `typ`, and expiry (with the configured clock skew). An access token stays active until it expires.

Failed caller authentication returns `401 invalid_client` and is written to the audit log as `introspection.authentication_failed`.

## CORS

Browser apps can call these endpoints directly:

- Discovery and JWKS allow any origin.
- The token, userinfo, and revocation endpoints allow the origins registered on enabled clients (`--cors-origin`). Credentials (cookies) are never allowed cross-origin.

The authorization endpoint is a browser navigation, not a CORS request. The introspection endpoint is for servers and doesn't allow CORS.
