# Getting started

::: warning Work in progress
ValkyrieID is under active development. This page describes what works today.
:::

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- SQLite (built in) or PostgreSQL

## Run from source

```sh
git clone https://github.com/ValkyrieID/valkyrieid.git
cd valkyrieid
dotnet run --project src/ValkyrieID.Server
```

In the `Development` environment, a development-only master key is preconfigured and the database is a SQLite file at `data/valkyrieid.db`.

Check that the server is up:

```sh
curl http://localhost:5102/health/ready
```

## Publish

```sh
dotnet publish src/ValkyrieID.Server -c Release -o publish
```

Before running the published output, generate a [master encryption key](/operations/master-key) and supply it:

```sh
export MasterKey__Current="$(openssl rand -base64 32)"   # store this somewhere safe!
./publish/valkyrieid
```

ValkyrieID refuses to start without a valid master key.

By default the server uses SQLite at `data/valkyrieid.db` relative to the working directory and applies database migrations on startup. See the [configuration reference](/reference/configuration) to use PostgreSQL.
