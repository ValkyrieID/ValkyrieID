# ValkyrieID

<img src="assets/branding/ValkyrieID.png" alt="ValkyrieID logo, a winged helmet with a keyhole" width="128" height="128">

A minimal, lightweight OpenID Connect / OAuth 2.0 identity server built on .NET.

> **Work in progress.** ValkyrieID is under active development and not ready for production use.

## Quick start

```sh
dotnet run --project src/ValkyrieID.Server
curl http://localhost:5102/health/ready
```

## Documentation

See **https://valkyrieid.liesel.dev**. The docs source is in [`docs/`](docs/).

## Development

```sh
dotnet build
dotnet test                 # PostgreSQL tests need Docker
dotnet format
```

The admin UI (Angular) is in [`src/ValkyrieID.AdminUI`](src/ValkyrieID.AdminUI); see the [admin UI guide](https://valkyrieid.liesel.dev/guide/admin-ui#building-from-source).

The specification and roadmap are in [`PROJECT.md`](PROJECT.md).

## License

MIT
