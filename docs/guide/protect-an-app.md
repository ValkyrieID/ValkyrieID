# Protect an app with oauth2-proxy

This guide puts a web app behind ValkyrieID. Browsers sign in with ValkyrieID, and services can call the app with access tokens from the `client_credentials` grant. [oauth2-proxy](https://oauth2-proxy.github.io/oauth2-proxy/) does the work, and [Caddy](https://caddyserver.com) asks it on every request whether to let the request through.

```
browser ──► Caddy ──forward_auth──► oauth2-proxy ──► ValkyrieID (sign-in)
service ──► Caddy ──forward_auth──► oauth2-proxy ──► checks the bearer token locally
               │
               └──► app (Authorization replaced by the app's own credentials)
```

ValkyrieID stays an identity provider. It doesn't know about the app, and the app doesn't need to speak OAuth. This works for apps that only support HTTP basic auth, such as a web UI that trusts a single username and password.

The examples use `app.example.com` for the protected app, `id.example.com` for ValkyrieID, and the API resource name `app.example.com` as the token audience. Replace them with your own names.

## 1. Register the clients and the API resource

```sh
valkyrieid resource create app.example.com --scope app.access

valkyrieid client create app-browser --grant authorization_code --grant refresh_token \
  --redirect-uri https://app.example.com/oauth2/callback \
  --scope openid --scope email --scope profile --scope offline_access

valkyrieid client create app-machine --scope app.access
```

- `app-browser` signs users in through oauth2-proxy. Store its `client_secret` where oauth2-proxy runs.
- `app-machine` is for services that call the app. It uses `client_credentials` by default. Give each service its own client so you can revoke one without affecting the others.
- The redirect URI must match exactly. ValkyrieID doesn't accept wildcards.

## 2. Run oauth2-proxy

Set these as environment variables, with the secrets kept out of version control:

```env
OAUTH2_PROXY_PROVIDER=oidc
OAUTH2_PROXY_OIDC_ISSUER_URL=https://id.example.com
OAUTH2_PROXY_CLIENT_ID=app-browser
OAUTH2_PROXY_CLIENT_SECRET=<app-browser secret>
OAUTH2_PROXY_REDIRECT_URL=https://app.example.com/oauth2/callback
OAUTH2_PROXY_CODE_CHALLENGE_METHOD=S256
OAUTH2_PROXY_SCOPE=openid email profile
OAUTH2_PROXY_COOKIE_SECRET=<32 random bytes, base64 encoded>
OAUTH2_PROXY_COOKIE_SECURE=true
OAUTH2_PROXY_WHITELIST_DOMAINS=app.example.com
OAUTH2_PROXY_EMAIL_DOMAINS=*
OAUTH2_PROXY_UPSTREAMS=static://202
OAUTH2_PROXY_HTTP_ADDRESS=0.0.0.0:4180
OAUTH2_PROXY_REVERSE_PROXY=true
OAUTH2_PROXY_TRUSTED_PROXY_IPS=172.30.0.10
OAUTH2_PROXY_SET_XAUTHREQUEST=true
OAUTH2_PROXY_SKIP_JWT_BEARER_TOKENS=true
OAUTH2_PROXY_OIDC_EXTRA_AUDIENCES=app.example.com
OAUTH2_PROXY_BEARER_TOKEN_LOGIN_FALLBACK=false
```

What the settings do:

- `CODE_CHALLENGE_METHOD=S256` sends PKCE with the sign-in request.
- `COOKIE_SECURE=true` keeps the session cookie HTTPS-only.
- `TRUSTED_PROXY_IPS` must be the Caddy container's address. Without it, oauth2-proxy trusts any source for forwarded headers.
- `UPSTREAMS=static://202` is a placeholder. In `forward_auth` mode, oauth2-proxy only answers the auth check, so it never proxies to the app.
- `SKIP_JWT_BEARER_TOKENS` lets oauth2-proxy accept a valid access token from ValkyrieID without a browser session. It checks the signature against ValkyrieID's keys, the issuer, the expiry, and that the audience is `app.example.com`.
- `BEARER_TOKEN_LOGIN_FALLBACK=false` makes an invalid bearer token get `403` instead of a redirect to sign-in.

`EMAIL_DOMAINS=*` lets any user who can sign in to ValkyrieID reach the app. See [Limit who can sign in](#limit-who-can-sign-in).

## 3. Put Caddy in front

`Caddyfile`:

```caddy
app.example.com {
	handle /oauth2/* {
		reverse_proxy oauth2-proxy:4180
	}

	handle {
		forward_auth oauth2-proxy:4180 {
			uri /oauth2/auth
			copy_headers X-Auth-Request-User X-Auth-Request-Email

			@unauthorized status 401
			handle_response @unauthorized {
				redirect /oauth2/start?rd={scheme}://{host}{uri}
			}
		}

		reverse_proxy app:8080 {
			# Remove the client's Authorization header, such as a bearer token,
			# so it never reaches the app.
			header_up -Authorization
			# Only needed if the app uses HTTP basic auth.
			header_up Authorization "Basic {env.APP_BASIC_AUTH}"
		}
	}
}
```

- `/oauth2/*` goes to oauth2-proxy so it can handle sign-in and the callback.
- Everything else is checked first. A `401` from oauth2-proxy turns into a redirect to sign-in. A `2xx` lets the request through.
- `copy_headers` passes the signed-in user's name and email to the app. The app should treat these headers as trusted only when they come from Caddy, so make sure the app can't be reached directly.
- `APP_BASIC_AUTH` is `base64("username:password")` for the app. Set it as an environment variable on the Caddy container, not in the Caddyfile.

## 4. Call the app from a service

Get a token with the machine client:

```sh
curl -u app-machine:$SECRET https://id.example.com/connect/token \
  -d grant_type=client_credentials -d scope=app.access
```

Send it to the app as a bearer token:

```sh
curl -H "Authorization: Bearer $ACCESS_TOKEN" https://app.example.com/some/path
```

Caddy removes the bearer token before the request reaches the app. Reuse the token until shortly before `expires_in` runs out instead of requesting one per call. See [Protect an API](/guide/protect-an-api) for the token details.

## Limit who can sign in

The default example lets every ValkyrieID user through. For an app with real access rules, restrict it. Options in oauth2-proxy include:

- `OAUTH2_PROXY_AUTHENTICATED_EMAILS_FILE`: a file with one allowed email address per line.
- `OAUTH2_PROXY_ALLOWED_GROUP`: a group name from the token's `groups` claim. Request the `groups` scope on `app-browser` and check that ValkyrieID includes the claim.

Check which option works with the claims ValkyrieID sends before relying on it.

## Limits

- **Scopes are not checked by oauth2-proxy.** It checks the audience but not individual scopes. Use one scope per API resource, or enforce the rest in the app.
- **Machine tokens reach the whole app.** Any valid token for `app.example.com` is accepted, so give each service its own client and keep its secret out of logs.
- **Unauthenticated service calls** get a redirect to sign-in, not a `401`, because the same gate also serves browsers. Services should always send a token.
- **Basic auth is only as strong as the app's password.** Use a long, unique password, and keep the app off the public internet except through Caddy.

## Related

- [Protect an API](/guide/protect-an-api): ASP.NET Core APIs that validate tokens themselves.
- [Docker](/guide/docker#behind-a-reverse-proxy): the Compose setup with Caddy in front of ValkyrieID.
- [Reverse proxy](/operations/reverse-proxy): trusted proxies and forwarded headers for ValkyrieID.
