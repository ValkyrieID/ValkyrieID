# Docker

ValkyrieID ships as a small container image for `linux/amd64` and `linux/arm64`, based on the [chiseled](https://learn.microsoft.com/dotnet/core/docker/container-images#scenario-based-images) ASP.NET Core runtime (`aspnet:10.0-noble-chiseled-extra`, with ICU and time zone data):

- no shell, package manager, or `curl`; the health check is the server's own [`healthcheck`](#health-check) command
- runs as the non-root user `app` (UID `1654`) and listens on port `8080`
- includes the [admin UI](/guide/admin-ui)
- contains no secrets; the master key and issuer are supplied at run time

::: tip Building the image
Images are published to `ghcr.io/valkyrieid/valkyrieid` from the first release (v0.1.0). Until then, build it from the repository, under the same name:

```sh
docker build -t ghcr.io/valkyrieid/valkyrieid .
# both architectures
docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/valkyrieid/valkyrieid .
```

The build restores NuGet and npm packages in locked mode (`packages.lock.json`, `npm ci`).
:::

## Quick start

ValkyrieID only runs over HTTPS: an OpenID Connect issuer must be an `https` URL (OIDC Discovery §3, RFC 8414 §2), also on your own machine. This quick start puts [Caddy](https://caddyserver.com) in front of it with a locally issued certificate, at `https://localhost:8443`.

In an empty directory, create `compose.yaml`:

```yaml
services:
  valkyrieid:
    image: ghcr.io/valkyrieid/valkyrieid
    restart: unless-stopped
    environment:
      Issuer__Url: https://localhost:8443
      MasterKey__Current: ${VALKYRIEID_MASTER_KEY:?set VALKYRIEID_MASTER_KEY in .env}
      ReverseProxy__KnownProxies__0: 172.30.0.10   # the Caddy container
    volumes:
      - valkyrieid-data:/data
    networks:
      internal:
        ipv4_address: 172.30.0.20

  caddy:
    image: caddy:2
    restart: unless-stopped
    ports:
      - "127.0.0.1:8443:8443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    networks:
      internal:
        ipv4_address: 172.30.0.10

networks:
  internal:
    ipam:
      config:
        - subnet: 172.30.0.0/24

volumes:
  valkyrieid-data:
    name: valkyrieid-data
  caddy-data:
```

and `Caddyfile`:

```
localhost:8443 {
	tls internal
	reverse_proxy valkyrieid:8080
}
```

Generate the [master key](/operations/master-key) into `.env` and start both containers:

```sh
echo "VALKYRIEID_MASTER_KEY=$(openssl rand -base64 32)" > .env
docker compose up -d
```

Then open `https://localhost:8443/setup` to [create the first administrator](/guide/first-run). The first start logs an EF Core warning (SQLite) or error (PostgreSQL) while creating the database; [both are expected](/guide/first-run#log-messages-on-the-first-start).

Caddy signs the certificate with its own local authority, so the browser warns about it. Accept the warning, or trust Caddy's root certificate on your machine:

```sh
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt caddy-root.crt
```

and import `caddy-root.crt` as a trusted root (for example in your browser's certificate settings). Apps that sign in with ValkyrieID need to trust it too.

This is for trying ValkyrieID locally. Keep `.env` safe: it holds the master key, and without it the data in the volume can't be used. For a public server, see [behind a reverse proxy](#behind-a-reverse-proxy) and the [production checklist](/operations/production-checklist).

## Defaults

| Setting | Image default |
|---|---|
| Port | `8080` (`ASPNETCORE_HTTP_PORTS`), plain HTTP; TLS is terminated by a proxy |
| Database | SQLite at `/data/valkyrieid.db` (`Database__Provider=Sqlite`, `Database__ConnectionString=Data Source=/data/valkyrieid.db`) |
| Volume | `/data`, owned by UID `1654` |
| User | `app` (UID `1654`) |
| Health check | `valkyrieid healthcheck` every 30 s |

Every other setting is configured with environment variables (`Section__Key`), see the [configuration reference](/reference/configuration). Required:

- `Issuer__Url`: the public URL, e.g. `https://id.example.com`
- `MasterKey__Current`: the [master encryption key](/operations/master-key)

With a bind mount instead of a named volume, the directory must be writable by UID `1654`:

```sh
mkdir -p ./data && sudo chown 1654:1654 ./data
```

## Behind a reverse proxy

In production, a proxy terminates TLS and forwards to port `8080` over a private network. ValkyrieID refuses every request that didn't arrive over HTTPS (except the health endpoints), so the proxy **must** be trusted with `ReverseProxy__KnownProxies` (or `ReverseProxy__KnownNetworks`). Otherwise its `X-Forwarded-Proto: https` header is ignored and every request gets `400 HTTPS is required`. See [reverse proxy](/operations/reverse-proxy) for the details.

This Compose example uses [Caddy](https://caddyserver.com), which obtains a certificate automatically and sends `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host`. Fixed addresses let ValkyrieID trust exactly the Caddy container; ValkyrieID's port isn't published, so clients can only reach it through Caddy. To put an app behind ValkyrieID with Caddy, see [Protect an app](/guide/protect-an-app).

`compose.yaml`:

```yaml
services:
  valkyrieid:
    image: ghcr.io/valkyrieid/valkyrieid
    restart: unless-stopped
    environment:
      Issuer__Url: https://id.example.com
      MasterKey__Current: ${VALKYRIEID_MASTER_KEY:?set VALKYRIEID_MASTER_KEY}
      ReverseProxy__KnownProxies__0: 172.30.0.10   # the Caddy container
    volumes:
      - valkyrieid-data:/data
    networks:
      internal:
        ipv4_address: 172.30.0.20

  caddy:
    image: caddy:2
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    networks:
      internal:
        ipv4_address: 172.30.0.10

networks:
  internal:
    ipam:
      config:
        - subnet: 172.30.0.0/24

volumes:
  valkyrieid-data:
  caddy-data:
```

`Caddyfile`:

```
id.example.com {
	reverse_proxy valkyrieid:8080
}
```

`.env` (keep it out of version control, readable only by you):

```sh
VALKYRIEID_MASTER_KEY=...   # openssl rand -base64 32
```

::: details Nginx Proxy Manager
With [Nginx Proxy Manager](https://nginxproxymanager.com), create a proxy host for `id.example.com` that forwards to `http://valkyrieid:8080` with an SSL certificate and "Force SSL" on. It sends `X-Forwarded-For` and `X-Forwarded-Proto` by default. Put both containers on a network with a fixed subnet as above and trust NPM's address (`ReverseProxy__KnownProxies__0`) or its network (`ReverseProxy__KnownNetworks__0=172.30.0.0/24`, only if nothing else on that network is reachable by clients). Keep NPM's admin port (`81`) off the internet and change its default credentials.
:::

## With PostgreSQL

```yaml
services:
  valkyrieid:
    image: ghcr.io/valkyrieid/valkyrieid
    restart: unless-stopped
    environment:
      Issuer__Url: https://id.example.com
      MasterKey__Current: ${VALKYRIEID_MASTER_KEY:?set VALKYRIEID_MASTER_KEY}
      Database__Provider: PostgreSql
      Database__ConnectionString: Host=db;Database=valkyrieid;Username=valkyrieid;Password=${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD};GSS Encryption Mode=Disable
      ReverseProxy__KnownProxies__0: 172.30.0.10
    depends_on:
      db:
        condition: service_healthy
    networks:
      internal:
        ipv4_address: 172.30.0.20

  db:
    image: postgres:18
    restart: unless-stopped
    environment:
      POSTGRES_DB: valkyrieid
      POSTGRES_USER: valkyrieid
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD}
    volumes:
      - postgres-data:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U valkyrieid -d valkyrieid"]
      interval: 5s
      timeout: 5s
      retries: 10
    networks:
      - internal

  # caddy: as in the reverse proxy example above

networks:
  internal:
    ipam:
      config:
        - subnet: 172.30.0.0/24

volumes:
  postgres-data:
```

Add `POSTGRES_PASSWORD` to `.env`. The image has no Kerberos libraries; `GSS Encryption Mode=Disable` stops the PostgreSQL driver from trying GSSAPI encryption (and logging that `libgssapi_krb5.so.2` is missing). Use `SSL Mode=Require` (or `VerifyFull`) when the database is on another host.

## Health check

The image's `HEALTHCHECK` runs `valkyrieid healthcheck` (exec form, no shell), which calls [`/health/ready`](/reference/health) over loopback and exits with `0` when it answers `200`, `1` otherwise. It doesn't touch the database or the master key itself, and works over plain HTTP (the health endpoints are the only ones that answer there) and with `ReverseProxy__PathBase`.

```sh
docker compose ps valkyrieid          # STATUS shows (healthy)
docker compose exec valkyrieid dotnet /app/valkyrieid.dll healthcheck --live
```

| Option | Description |
|---|---|
| `--ready` | Probe `/health/ready` (default): database and, when configured, Redis. |
| `--live` | Probe `/health/live`: the process serves requests. |
| `--url <url>` | Probe this base URL instead, e.g. `http://127.0.0.1:8080/auth`. |
| `--timeout <seconds>` | Request timeout, default `5`. |

The address comes from `ASPNETCORE_HTTP_PORTS`, `ASPNETCORE_URLS`, or `Kestrel:Endpoints` (the first plain-HTTP one), like the server's own. In Kubernetes, use HTTP probes on `/health/live` and `/health/ready` instead.

::: info Podman
`podman build` writes the OCI image format by default, which has no `HEALTHCHECK`. Build with `podman build --format docker`, or define the check in Compose (`healthcheck: test: ["CMD", "dotnet", "/app/valkyrieid.dll", "healthcheck"]`).
:::

## CLI commands

The [CLI](/reference/cli) runs in the same image. In the running container, the environment is already set:

```sh
docker compose exec valkyrieid dotnet /app/valkyrieid.dll user list
docker compose exec valkyrieid dotnet /app/valkyrieid.dll admin reset-password alice
```

## Volumes and backups

With SQLite, everything ValkyrieID stores is in the `/data` volume; with PostgreSQL, it's in the database server. Only the files in [`Branding:Directory`](/reference/configuration#branding) live elsewhere, if you use them. Back up the database regularly, and keep the [master key](/operations/master-key) with the backup: signing keys, TOTP secrets, and the SMTP password in the database can't be decrypted without it.

See [Backups](/operations/backups) for consistent backups while the container runs, restoring, and what to keep, and [Upgrades](/operations/upgrades) before pulling a new image.

## Footprint

Measured on each release (see [footprint measurements](#measuring-the-footprint)):

| Metric | Target | Measured (amd64) |
|---|---|---|
| Image size | < 210 MB | 180 MB |
| Startup to ready (SQLite, fresh volume) | < 4 s | 2.5–3.0 s |
| Idle memory | < 150 MB | ~100 MB |

The chiseled base with ICU and time zone data is 163 MB of the image; ValkyrieID and its dependencies add about 25 MB.

### Measuring the footprint

`footprint/measure.sh` (Linux, macOS) and `footprint/measure.ps1` (Windows) build the image, start it with SQLite on a fresh volume, time `docker start` until `/health/ready` answers `200`, run `valkyrieid healthcheck` inside the container, wait 30 seconds, and read the memory with `docker stats`. They exit with `1` when a target is exceeded.

```sh
sh footprint/measure.sh                       # build and measure
sh footprint/measure.sh --image ghcr.io/valkyrieid/valkyrieid --skip-build
pwsh footprint/measure.ps1 -SkipBuild -Image ghcr.io/valkyrieid/valkyrieid
```

The `Container` GitHub Actions workflow runs the same script on release tags and on manual runs, after building the image for both architectures.
