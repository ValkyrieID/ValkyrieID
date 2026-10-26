# Endpoints

All URLs are relative to the configured [issuer](/reference/configuration#issuer).

| Endpoint | Method | Spec |
|---|---|---|
| `/.well-known/openid-configuration` | `GET` | OpenID Connect Discovery 1.0, RFC 8414 |
| `/.well-known/jwks.json` | `GET` | RFC 7517 |
| `/connect/token` | `POST` | RFC 6749 §3.2 |

The authorization, userinfo, revocation, introspection, and end session endpoints arrive in later releases. Discovery only advertises what is implemented.

## Discovery

Returns the issuer, `jwks_uri`, `token_endpoint`, `grant_types_supported` (`client_credentials`), and `token_endpoint_auth_methods_supported` (`client_secret_basic`, `client_secret_post`).

## JWKS

Returns the public RSA keys (`kty`, `use`, `alg`, `kid`, `n`, `e`). This includes keys that are not used yet and retired keys whose tokens may still be valid. See [Signing key rotation](/operations/key-rotation).

## Token endpoint

Requests must be `POST` with `application/x-www-form-urlencoded` parameters. Parameters must not be repeated. Responses are JSON with `Cache-Control: no-store`.

### Client authentication

Confidential clients authenticate with one of:

- **`client_secret_basic`**: HTTP Basic, with the client id and secret each form-urlencoded first (RFC 6749 §2.3.1).
- **`client_secret_post`**: `client_id` and `client_secret` form parameters.

Using both in the same request is rejected.

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

### Errors

Errors follow RFC 6749 §5.2:

| Error | Status | Cause |
|---|---|---|
| `invalid_request` | 400 | Wrong content type, repeated or missing parameters, or two authentication methods |
| `invalid_client` | 401 | Client authentication failed (unknown or disabled client, wrong or expired secret) |
| `unauthorized_client` | 400 | The client may not use this grant type |
| `unsupported_grant_type` | 400 | The grant type isn't supported |
| `invalid_scope` | 400 | A scope is malformed, unknown, disabled, an identity scope, or not allowed for the client |

All client authentication failures return the same response, so callers can't tell which clients exist.
