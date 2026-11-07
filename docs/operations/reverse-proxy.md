# Reverse proxy

ValkyrieID can run behind a reverse proxy or load balancer that terminates TLS (nginx, Traefik, Caddy, a cloud load balancer).

## Issuer

Set `Issuer:Url` to the public URL clients use, e.g. `https://id.example.com`. Discovery, tokens, and every endpoint URL use it. It is never derived from `Host` or forwarded headers, so a spoofed header can't change the issuer.

## Trusted proxies

Behind a proxy, the server sees the proxy's address instead of the client's. That breaks per-IP [rate limits](/reference/configuration#rate-limits) (all clients share one bucket) and makes audit log IP addresses useless. Configure the proxies whose `X-Forwarded-For` and `X-Forwarded-Proto` headers ValkyrieID may trust:

```sh
ReverseProxy__KnownProxies__0=10.0.0.5
# or a whole network
ReverseProxy__KnownNetworks__0=10.0.0.0/24
```

Forwarded headers from any other address are ignored, and without any configured proxy they are always ignored. Only trust addresses that clients can't reach directly; otherwise a client could claim any IP address.

`ReverseProxy:ForwardLimit` (default `1`) is the number of proxies in front of ValkyrieID. With a CDN in front of a load balancer, set it to `2` and trust both.

With [several instances](/operations/multi-instance), give every instance the same trusted proxies. Rate limits are shared through Redis and keyed by client address, so an instance that trusts different proxies would count clients under other keys.

Make sure the proxy **replaces** `X-Forwarded-For` with the client address (or appends to it) and sets `X-Forwarded-Proto: https`. The session cookie is `Secure`, so the server must see HTTPS requests as HTTPS.

::: warning HTTPS is required
While `Issuer:Url` is `https`, ValkyrieID refuses every request that didn't arrive over HTTPS with `400` (OAuth 2.0 requires TLS for the authorization and token endpoints, RFC 6749 3.1 and 3.2). Requests aren't redirected, because their credentials or codes have already crossed the network. Only the [health endpoints](/reference/health) answer over plain HTTP. Behind a TLS-terminating proxy, the proxy must therefore be a trusted proxy that sends `X-Forwarded-Proto: https`; otherwise every request is refused. Keep the connection between the proxy and ValkyrieID on a private network (RFC 9700 4.13).
:::

The `Strict-Transport-Security` header is sent on HTTPS responses (see [HSTS](/reference/configuration#hsts)).

## Path base

If ValkyrieID is served under a path such as `https://example.com/auth`:

- If the proxy strips `/auth` before forwarding, only set `Issuer:Url=https://example.com/auth`.
- If the proxy forwards the full path, also set `ReverseProxy:PathBase=/auth`.

`ReverseProxy:PathBase` must equal the path of `Issuer:Url`; the server doesn't start otherwise. Cookies keep `Path=/`, as the `__Host-` prefix requires, so don't host other apps that set cookies with the same names on the same host.

The [admin UI](/guide/admin-ui) takes its URLs from `Issuer:Url`, so it works under a path base either way.

## Keeping administration internal

To keep the admin UI and the Admin API off the internet, block both paths at the proxy and administer from an internal address (or with the [CLI](/reference/cli)). Sign-in still goes through the public issuer, which only the browser needs to reach.

```nginx
location /admin     { allow 10.0.0.0/8; deny all; proxy_pass http://valkyrieid:8080; }
location /api/admin { allow 10.0.0.0/8; deny all; proxy_pass http://valkyrieid:8080; }
```

If you don't need them at all, turn them off with `AdminUI__Enabled=false` or `AdminApi__Enabled=false` ([configuration](/reference/configuration#admin-api-and-admin-ui)).

## nginx example

```nginx
location / {
    proxy_pass         http://valkyrieid:8080;
    proxy_set_header   Host              $host;
    proxy_set_header   X-Forwarded-For   $remote_addr;
    proxy_set_header   X-Forwarded-Proto $scheme;
}
```

```sh
Issuer__Url=https://id.example.com
ReverseProxy__KnownProxies__0=172.18.0.2   # the nginx container
```

For a complete Compose setup with Caddy (and a note on Nginx Proxy Manager), see [Docker](/guide/docker#behind-a-reverse-proxy).
