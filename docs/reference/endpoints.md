# Endpoints

All URLs are relative to the configured [issuer](/reference/configuration#issuer).

| Endpoint | Method | Spec |
|---|---|---|
| `/.well-known/openid-configuration` | `GET` | OpenID Connect Discovery 1.0, RFC 8414 |
| `/.well-known/jwks.json` | `GET` | RFC 7517 |
| `/connect/token` | `POST` | RFC 6749 §3.2 |

The authorization, userinfo, revocation, introspection, and end session endpoints arrive in later releases. Discovery only advertises what is implemented.

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
| `scopes_supported` | `openid`, `profile`, `email`, `roles`, `groups` |
| `response_types_supported` | `code` |
| `response_modes_supported` | `query`, `form_post` |
| `grant_types_supported` | `authorization_code`, `client_credentials` |
| `token_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post`, `none` |
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
| `scope` | yes | Space-separated. Include `openid` for OpenID Connect; without it the request is plain OAuth 2.0 and returns only an access token. In OpenID Connect requests, scopes the server doesn't know (e.g. `address`) are ignored; scopes that exist but aren't allowed for the client are rejected. `offline_access` is ignored until refresh tokens are supported. |
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

The access token's `sub` is the user's id and its `aud` is the API resources that own the granted API scopes, or the client id if no API scope was granted. `id_token` is included when `openid` was granted.

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
| `invalid_grant` | 400 | The code is unknown, expired, already used, issued to another client or redirect URI, the PKCE verifier doesn't match, or the session has ended |
| `unauthorized_client` | 400 | The client may not use this grant type |
| `unsupported_grant_type` | 400 | The grant type isn't supported |
| `invalid_scope` | 400 | A scope is malformed, unknown, disabled, an identity scope, or not allowed for the client |

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

## CORS

Browser apps can call these endpoints directly:

- Discovery and JWKS allow any origin.
- The token and userinfo endpoints allow the origins registered on enabled clients (`--cors-origin`). Credentials (cookies) are never allowed cross-origin.

The authorization endpoint is a browser navigation, not a CORS request.
