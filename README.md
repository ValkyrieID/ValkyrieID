# ValkyrieID

<img src="assets/branding/ValkyrieID.png" alt="ValkyrieID logo, a winged shield" width="128" height="128">

A minimal, lightweight OpenID Connect / OAuth 2.0 identity server built on .NET.

> **Work in progress.** ValkyrieID is under active development and not ready for production use.

## Quick start

```sh
docker build -t valkyrieid .
docker run -p 127.0.0.1:8080:8080 -v valkyrieid-data:/data \
  -e Issuer__Url=http://localhost:8080 -e MasterKey__Current="$(openssl rand -base64 32)" valkyrieid
```

Then open http://localhost:8080/setup. See [Docker](https://valkyrieid.liesel.dev/guide/docker) for production setups, or run from source:

```sh
dotnet run --project src/ValkyrieID.Server
```

## Documentation

See **https://valkyrieid.liesel.dev**. The docs source is in [`docs/`](docs/).

## Development

```sh
dotnet build
dotnet test                 # PostgreSQL and Redis tests need Docker
dotnet format
```

CI runs the tests on Linux. To catch Linux-only failures (case-sensitive paths, `Uri` parsing) before pushing, run the SQLite tests in a container:

```sh
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test -c Release --artifacts-path /tmp/artifacts -- --filter-not-trait Category=PostgreSql --filter-not-trait Category=Redis
```

The admin UI (Angular) is in [`src/ValkyrieID.AdminUI`](src/ValkyrieID.AdminUI); see the [admin UI guide](https://valkyrieid.liesel.dev/guide/admin-ui#building-from-source).

Accessibility checks (Playwright and axe, WCAG 2.2 AA) are in [`tests/ValkyrieID.A11yTests`](tests/ValkyrieID.A11yTests): `npm ci`, `npx playwright install chromium`, `npm test`. See [Accessibility](https://valkyrieid.liesel.dev/accessibility/#running-the-checks).

The specification and roadmap are in [`PROJECT.md`](PROJECT.md).

## License

MIT
