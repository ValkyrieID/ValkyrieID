# Getting started

::: warning Pre-1.0
ValkyrieID hasn't reached 1.0 yet: configuration and APIs may change between 0.x releases. Before going live, work through the [production checklist](/operations/production-checklist).
:::

## Requirements

- [Docker](/guide/docker), or
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- SQLite (built in) or PostgreSQL

## Run with Docker

Follow the [Docker quick start](/guide/docker#quick-start): a Compose file runs ValkyrieID behind Caddy at `https://localhost:8443`. Until the first release is published, build the image first:

```sh
docker build -t ghcr.io/valkyrieid/valkyrieid .
```

ValkyrieID only serves over HTTPS, also locally: the issuer must be an `https` URL (OIDC Discovery §3, RFC 8414 §2).

## Run from source

```sh
git clone https://github.com/ValkyrieID/valkyrieid.git
cd valkyrieid
dotnet dev-certs https --trust   # once, for https://localhost
dotnet run --project src/ValkyrieID.Server
```

In the `Development` environment, the issuer is `https://localhost:7196`, a development-only master key is preconfigured, and the database is a SQLite file at `data/valkyrieid.db`. The server also listens on `http://localhost:5102`, but only the health endpoints answer there.

Check that the server is up:

```sh
curl https://localhost:7196/health/ready
```

## Publish

```sh
dotnet publish src/ValkyrieID.Server -c Release -o publish
```

Before running the published output, generate a [master encryption key](/operations/master-key) and set the public [issuer URL](/reference/configuration#issuer):

```sh
export MasterKey__Current="$(openssl rand -base64 32)"   # store this somewhere safe!
export Issuer__Url="https://id.example.com"
./publish/valkyrieid
```

ValkyrieID refuses to start without a valid master key and issuer URL.

By default the server uses SQLite at `data/valkyrieid.db` relative to the working directory and applies database migrations on startup. See the [configuration reference](/reference/configuration) to use PostgreSQL.

Next, open `/setup` to [create the first administrator](/guide/first-run), or [protect an API](/guide/protect-an-api) with ValkyrieID access tokens.

## Development

```sh
dotnet build
dotnet test                 # PostgreSQL and Redis tests need Docker (or Podman)
dotnet format
```

CI runs the tests on Linux. To catch Linux-only failures (case-sensitive paths, `Uri` parsing) before pushing, run the SQLite tests in a container:

```sh
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test -c Release --artifacts-path /tmp/artifacts -- --filter-not-trait Category=PostgreSql --filter-not-trait Category=Redis
```

- The admin UI (Angular) is in `src/ValkyrieID.AdminUI`; see [building from source](/guide/admin-ui#building-from-source).
- Accessibility checks (Playwright and axe, WCAG 2.2 AA) are in `tests/ValkyrieID.A11yTests`; see [running the checks](/accessibility/#running-the-checks).
- The specification and roadmap are in [`PROJECT.md`](https://github.com/ValkyrieID/valkyrieid/blob/main/PROJECT.md), and the rules for contributors and coding agents in [`AGENTS.md`](https://github.com/ValkyrieID/valkyrieid/blob/main/AGENTS.md).
