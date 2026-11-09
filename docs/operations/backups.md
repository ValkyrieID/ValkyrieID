# Backups

## What to back up

| What | Where | Why |
|---|---|---|
| The database | SQLite: the `/data` volume (`valkyrieid.db`). PostgreSQL: the database server. | Users, clients, API resources, sessions, refresh tokens, signing keys, settings, the audit log, and the Data Protection keys behind cookies. |
| The [master key](/operations/master-key) | Your secret store (`MasterKey:Current`, and `MasterKey:Previous:*` during a rotation). | Secrets in the database are encrypted with it. It is never stored in the database. |
| The branding files | [`Branding:Directory`](/reference/configuration#branding), if set. | Logos, wallpapers, and stylesheets are files, not database rows. The database only stores their names; without the files, pages show broken images, and the server refuses to start if the directory itself is missing. |
| The configuration | `appsettings.json`, environment variables, Compose files. | Everything that isn't a [runtime setting](/reference/configuration#runtime-settings). Keep secrets out of the backup and in the secret store. |

Authorization codes and rate-limit counters (in memory, or in [Redis](/operations/multi-instance)) don't need a backup: losing them only means users in the middle of signing in start again.

::: danger Keep the master key with the database
Back up the key separately from the database (so one stolen backup doesn't hold both), but keep them together in your restore plan. A database backup without its master key can't be used.
:::

## Without the master key

Restoring a database with a different master key doesn't work: everything encrypted with the old key is unreadable, and there is no way to recover it. That includes:

- the **signing keys**: no tokens can be issued or validated with them
- the **Data Protection keys**: every session cookie and form token becomes invalid
- **TOTP secrets**: users with 2FA can't complete sign-in
- the **SMTP password** set as a runtime setting, and **queued emails**

If the key is lost, you can only start over with a new database. Users, clients, and resources would have to be created again, and every client would get new secrets.

## SQLite

The database is a single file, `/data/valkyrieid.db` in the [container image](/guide/docker#defaults). The commands below use the [quick start](/guide/docker#quick-start) Compose setup, whose volume is named `valkyrieid-data`; they run a short-lived Alpine container on the same volume, since the image itself has no shell or `sqlite3`.

### While the server runs

Use SQLite's online backup, which produces a consistent copy even while the server writes:

```sh
mkdir -p backup
docker run --rm -v valkyrieid-data:/data -v "$PWD/backup:/backup" alpine:3 \
  sh -c 'apk add --no-cache -q sqlite && sqlite3 /data/valkyrieid.db ".backup /backup/valkyrieid.db"'
```

Don't copy `valkyrieid.db` with `cp` while the server runs: the copy can be inconsistent.

### With the server stopped

```sh
docker compose stop valkyrieid
docker run --rm -v valkyrieid-data:/data -v "$PWD/backup:/backup" alpine:3 cp -a /data/. /backup/
docker compose start valkyrieid
```

### Restore

Stop the server, replace the database file, and start it again. The file must belong to UID `1654`:

```sh
docker compose stop valkyrieid
docker run --rm -v valkyrieid-data:/data -v "$PWD/backup:/backup" alpine:3 \
  sh -c 'rm -f /data/valkyrieid.db-journal && cp /backup/valkyrieid.db /data/valkyrieid.db && chown 1654:1654 /data/valkyrieid.db'
docker compose start valkyrieid
```

Start it with the same master key the backup was taken with.

## PostgreSQL

Use `pg_dump`. With the [Compose example](/guide/docker#with-postgresql):

```sh
docker compose exec -T db pg_dump -U valkyrieid -d valkyrieid -Fc > valkyrieid.dump
```

`pg_dump` takes a consistent snapshot while ValkyrieID keeps running. Use a `pg_dump` at least as new as the server.

### Restore

Stop ValkyrieID (every instance) so nothing writes during the restore, then restore into the database:

```sh
docker compose stop valkyrieid
docker compose exec -T db pg_restore -U valkyrieid -d valkyrieid --clean --if-exists --no-owner < valkyrieid.dump
docker compose start valkyrieid
```

If you use [Redis](/operations/multi-instance), authorization codes stored there expire within 60 seconds; wait at least that long before starting ValkyrieID again, or flush the keys with your `Redis:KeyPrefix`.

## After a restore

Everything after the backup is lost, so:

- Users who changed their password, set up 2FA, or were created after the backup are back to the old state.
- Sessions and refresh tokens issued after the backup no longer exist; those users sign in again.
- A signing key created after the backup is gone. Tokens it signed fail validation, and clients receive new ones at their next sign-in or refresh.
- Revocations after the backup are undone: revoke the affected sessions or secrets again.

Test the restore regularly, for example into a throwaway container on a separate volume.
