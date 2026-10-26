# ValkyrieID

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

The specification and roadmap are in [`PROJECT.md`](PROJECT.md).

## License

MIT
