# Production checklist

Work through this list before going live, and again after major changes.

## Network and HTTPS

- [ ] **Issuer URL.** `Issuer__Url` is the public `https` URL that clients use, e.g. `https://id.example.com`. It never comes from request headers, and changing it later invalidates every issued token and every client's configuration. → [Issuer](/reference/configuration#issuer)
- [ ] **TLS at the proxy.** A reverse proxy terminates TLS and forwards to port `8080` over a private network; ValkyrieID's port isn't published to the internet. → [Docker: behind a reverse proxy](/guide/docker#behind-a-reverse-proxy)
- [ ] **Trusted proxies.** `ReverseProxy__KnownProxies` (or `KnownNetworks`) lists exactly your proxy, so `X-Forwarded-Proto` and client IPs are trusted from it and from nothing else. Without it, every request gets `400 HTTPS is required`. → [Reverse proxy](/operations/reverse-proxy#trusted-proxies)
- [ ] **HSTS.** On by default outside `Development`. Only turn on `Hsts:IncludeSubDomains` or `Preload` if every subdomain serves HTTPS. → [HSTS](/reference/configuration#hsts)
- [ ] **Environment.** `ASPNETCORE_ENVIRONMENT` isn't `Development` (the image default is `Production`). → [Hosting](/reference/configuration#hosting)
- [ ] **Security headers.** Check with your browser's developer tools that the CSP, `X-Frame-Options`, and HSTS headers reach the browser, and that the proxy doesn't remove or replace them. → [Endpoints: pages](/reference/endpoints#pages)
- [ ] **Administration internal (optional).** Restrict `/admin` and `/api/admin` to internal networks at the proxy, or turn them off with `AdminUI:Enabled` / `AdminApi:Enabled`. → [Keeping administration internal](/operations/reverse-proxy#keeping-administration-internal)

## Secrets and keys

- [ ] **Master key** generated with a secure random generator, stored in a secret store, and supplied as an environment variable or secret, never committed. → [Master encryption key](/operations/master-key)
- [ ] **Master key rotation** is planned and you know the steps (`MasterKey:Previous`, `admin reencrypt`). → [Rotate the key](/operations/master-key#rotate-the-key)
- [ ] **Signing key rotation.** Keys rotate automatically every 90 days. Your APIs read the JWKS and don't pin a single key. → [Signing key rotation](/operations/key-rotation)
- [ ] **Client secrets** are handed out over a secure channel and rotated without downtime by adding the new secret before removing the old one. → [Rotating a client secret](/reference/cli#rotating-a-client-secret)

## Accounts

- [ ] **Admin 2FA.** `AdminApi:RequireTwoFactor` is on (the default), and every admin has set up 2FA and stored their recovery codes. → [Two-factor authentication](/guide/two-factor#require-2fa)
- [ ] **A second admin** or the [CLI break-glass commands](/reference/cli#admin-recovery), so losing one account doesn't lock you out.
- [ ] **Password policy** fits your users; consider `PasswordPolicy:CheckBreachedPasswords`. → [Passwords](/reference/configuration#passwords)
- [ ] **Rate limits and lockout** fit your expected traffic; behind a proxy, limits only work per client with trusted proxies configured. → [Rate limits](/reference/configuration#rate-limits), [Lockout](/reference/configuration#lockout)

## Email

- [ ] **SMTP** is configured with `StartTls` or `Tls` and a sender address, and a test email arrives (`admin send-test-email`). Without SMTP, users can't reset their own passwords. → [Email](/guide/email#configure-smtp)

## Scaling

- [ ] **Single instance:** SQLite on a persistent volume is fine.
- [ ] **Several instances:** PostgreSQL and Redis (`maxmemory-policy noeviction`), the same configuration on every instance, and `/health/ready` as the load balancer check. → [Multiple instances](/operations/multi-instance)

## Operations

- [ ] **Backups** of the database, the master key, and the branding directory run regularly, and a restore was tested. → [Backups](/operations/backups)
- [ ] **Upgrades:** an exact image version is pinned, and you know the rollback limits. → [Upgrades](/operations/upgrades)
- [ ] **Logging** goes to a central place; logs are JSON. → [Observability](/operations/observability#logging)
- [ ] **Monitoring:** `/health/live` and `/health/ready` are checked, and optionally traces and metrics go to an OpenTelemetry collector. → [Health endpoints](/reference/health), [OpenTelemetry](/operations/observability#opentelemetry)
- [ ] **Audit log** retention (`AuditLog:Retention`, 365 days by default) fits your requirements. → [Audit log](/reference/configuration#audit-log)
