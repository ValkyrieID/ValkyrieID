# Upgrades

::: warning Pre-1.0
Until 1.0, a 0.x release may change configuration or behavior. Read the [changelog](https://github.com/ValkyrieID/valkyrieid/blob/main/CHANGELOG.md) for every version between yours and the new one before upgrading.
:::

## Image tags

Releases are published as `ghcr.io/valkyrieid/valkyrieid:<version>` (from v0.1.0). Pin an exact version in production, and upgrade deliberately instead of following `latest`. Check which version is running with:

```sh
docker compose exec valkyrieid dotnet /app/valkyrieid.dll --version
```

## Upgrade steps

1. **Back up** the database and check that you have the master key ([Backups](/operations/backups)). This is your only way back (see [Rolling back](#rolling-back)).
2. Read the changelog.
3. Pull the new image and recreate the container with the same configuration and volume. With Compose, set the new tag in `compose.yaml` (`image: ghcr.io/valkyrieid/valkyrieid:<version>`), then:

   ```sh
   docker compose pull valkyrieid
   docker compose up -d valkyrieid
   ```
4. Watch the logs and wait until [`/health/ready`](/reference/health) answers `200`.

## Database migrations

The server applies pending database migrations when it starts, before it serves requests. Every [CLI command](/reference/cli) does the same, except `healthcheck`, `--help`, and `--version`. The log shows how many migrations are applied. If a migration fails, the server stops and doesn't serve with a half-migrated database; restore the backup and report the problem.

Leave [`Database:AutoMigrate`](/reference/configuration#database) on. ValkyrieID ships no separate migration tool, so with it off you would have to apply migrations from a source checkout with `dotnet ef`.

EF Core locks the database while it migrates, so several instances starting at once apply each migration only once.

## Rolling back

Migrations only go forward: there is no command to undo them, and an older version doesn't check whether the database was migrated by a newer one. Running an old image against a migrated database can fail in unpredictable ways.

To go back to the previous version, stop the server, **restore the backup** taken before the upgrade, and start the old image. Changes made since the upgrade are lost (see [After a restore](/operations/backups#after-a-restore)).

## Multiple instances

With [several instances](/operations/multi-instance) on PostgreSQL and Redis:

- **No database migrations in the release** (the changelog says so): upgrade one instance at a time. Old and new instances can serve side by side. They share the database, Redis, cookies, and signing keys.
- **With migrations:** the first new instance migrates the database while old instances still run. Unless the changelog says the migration is safe for a rolling upgrade, stop all instances, upgrade, and start them again. Expect a short outage.

Back up first either way. During a rolling upgrade, users who are in the middle of signing in may have to start again.
