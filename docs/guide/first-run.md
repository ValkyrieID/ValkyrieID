# First run

When the database has no users, ValkyrieID serves a setup wizard at `/setup` to create the first administrator.

::: warning Finish setup right away
Until the first user exists, anyone who can reach `/setup` can create the administrator account. Complete setup before exposing the server, or create the first admin with the CLI instead (see below).
:::

1. Start the server (see [Getting started](/guide/getting-started)).
2. Open `https://<your issuer>/setup`.
3. Enter a username, an optional email address, and a password that meets the [password policy](/reference/configuration#passwords).
4. Select **Create administrator**.
5. Set up [two-factor authentication](/guide/two-factor): scan the QR code with an authenticator app, enter a code, and save the recovery codes. Administrators need it to use the admin UI ([`AdminApi:RequireTwoFactor`](/reference/configuration#admin-api-and-admin-ui)).

You are then signed in and redirected to `/account`. From then on, `/setup` returns `404`. The wizard can't be run again, even if every user is later disabled.

## Log messages on the first start

When the database is new, ValkyrieID creates the schema (`Applying 12 pending database migration(s)`). Two messages from EF Core look alarming but are expected:

- **PostgreSQL:** an `Error` from `Microsoft.EntityFrameworkCore.Database.Command`, `Failed executing DbCommand ... FROM "__EFMigrationsHistory"`. EF Core reads the migrations table before it exists, then creates it.
- **SQLite:** a `Warning` (event `20410`) that `PRAGMA foreign_keys = 0` from migration `RefreshTokens` can't run in a transaction. SQLite requires this outside a transaction to rebuild a table; it only matters if the process is killed during that migration on the very first start. In that case, delete the database file and start again.

Neither appears on later starts. If the server reports `/health/ready` as healthy, the migrations succeeded.

## Without the wizard

You can also create the first admin from the command line. The wizard then never appears:

```sh
./valkyrieid user create admin --email admin@example.com
./valkyrieid admin grant-admin admin
```

See the [CLI reference](/reference/cli#users). The admin sets up two-factor authentication at their first sign-in.

## Signing in

Users sign in at `/account/login` with their username or email address. After too many failed attempts the account is [locked](/reference/configuration#lockout) for a while. Failed sign-ins always show the same message, so the page doesn't reveal which accounts exist.

If an admin is locked out or has forgotten their password, use the [admin recovery commands](/reference/cli#admin-recovery).

## Administration

At startup the server creates the built-in `valkyrieid-admin-ui` client and `valkyrieid-admin` API resource, which the admin UI uses to call the [Admin API](/reference/admin-api). Admins (users with the `admin` role) can then manage users, clients, and API resources over the Admin API, as well as with the [CLI](/reference/cli).
