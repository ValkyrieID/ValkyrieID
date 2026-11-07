# OAuch results

[OAuch](https://oauch.io) tests an OAuth 2.0 / OpenID Connect authorization server against the security requirements of the OAuth standards and the threat models of RFC 6819 and RFC 9700. It needs a publicly reachable server and a person to sign in, so it is run manually, not in CI.

## Latest run

| | |
|---|---|
| Date | 2026-11-07 |
| ValkyrieID | commit `f52a4b9` plus the fragment fix below, container image, SQLite |
| OAuch | hosted at oauch.io, version 2024.11.28 |
| Documents | all except FAPI 1.0 (ValkyrieID doesn't target FAPI) |
| Flows | authorization code (with PKCE `S256`) and client credentials; the others are excluded because ValkyrieID doesn't support them |
| Client | confidential, `client_secret_basic`, with a second client for the mix-up tests |

Of 147 test cases, 93 passed (including the authorization code and access token expiry tests), 41 were skipped as not applicable, and 13 reported a missing countermeasure or feature. The fragment test failed in the first pass and passed when retried after the fix. The findings:

| Test | Result | Resolution |
|---|---|---|
| Does the server attach a fragment | Not implemented, then passed | **Fixed.** Every redirect to a client now ends with `#_` (RFC 9700 §4.1.3, a MAY), so browsers can't reattach a fragment from the request URL. Redirect URIs can't contain fragments (RFC 6749 §3.1.2), so nothing clashes. |
| Are access tokens invalidated after exchanging the same authorization code multiple times | Not implemented | **Accepted** ([spec deviation](#accepted-findings), RFC 6749 §4.1.2). The replay is denied and audited, and the refresh tokens from the code are revoked; self-contained access tokens can't be revoked and expire within their lifetime. |
| Are invalid scopes / response types automatically redirected back | Redirected | **Accepted.** RFC 6749 §4.1.2.1 and OIDC Core §3.1.2.6 require the error to be returned to a valid redirect URI. Redirect URIs are registered by administrators only and matched exactly, so they are trusted (RFC 9700 §4.11.2). |
| Can access tokens be revoked | No | **Accepted** (spec deviation RFC 7009 §2). |
| Are bearer tokens disabled (sender-constrained tokens) | No | **Accepted** (spec deviation RFC 9700 §2.2.1); DPoP is planned for phase 11b. |
| JWT / asymmetric client authentication | Not supported | **Accepted** (spec deviation RFC 9700 §2.5); `private_key_jwt` is planned for phase 11a. |
| RFC 7523, RFC 8628, RFC 8705 supported | No | Not applicable: JWT client authentication (11a), the device grant, and mTLS aren't offered. |
| Plain PKCE, deprecated TLS versions, access tokens in the query string | Not supported | Good: these are deprecated features, and ValkyrieID rejects them. |

The concurrency tests (parallel code exchanges and refresh token use) were skipped because OAuch couldn't obtain two token sets from one code or refresh token, which is the intended behavior.

## Accepted findings

These are recorded in the `PROJECT.md` "Spec deviations" table and the [security checklist](/security/checklist#accepted-deviations).

## Running OAuch

`security/oauch/run.ps1` starts a throwaway instance behind your TLS-terminating proxy and registers the test user and clients:

```powershell
security/oauch/run.ps1 discover                      # shows the address the proxy connects from
security/oauch/run.ps1 start -KnownProxy <proxy ip> -Issuer https://id.example.com
security/oauch/run.ps1 restart                       # after rebuilding the image; keeps the clients and secrets
security/oauch/run.ps1 stop                          # removes the container, its database, and the secrets
```

The instance listens on plain HTTP (port 5102 by default); the proxy must send `X-Forwarded-Proto: https`, and only its address is trusted. Allow only the proxy through the firewall. The script prints the client ids, secrets, and the test user's password, and saves them in `security/oauch/out/` (git-ignored).

In OAuch, add a site with the discovery URL and **All available tests**, then in the site settings:

- **Site Settings:** exclude every flow except `code` and `client_credentials`.
- **Client Settings:** `oauch-1` and its secret, scope `openid profile email offline_access`; the alternative client `oauch-2`.
- **Grant-specific Overrides → Client Credentials:** scope `oauch.read`.
- **API Settings:** API Test URI `<issuer>/connect/userinfo`, method GET.
- **OpenID Connect:** the issuer and `<issuer>/.well-known/jwks.json`.
- **Advanced Settings:** PKCE `S256`, client authentication `client_secret_basic`.
- **Documents:** deselect FAPI1Base and FAPI1Adv.

During the run, sign in as `oauch`. When ValkyrieID shows an error page instead of redirecting (an altered `redirect_uri`, PKCE downgrade, unsupported `response_type`), go back to OAuch and click **stalled test**: refusing those requests is the expected behavior. Resume the run after the waiting period OAuch shows, so the expiry tests complete.
