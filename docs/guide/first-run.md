# First run

When the database has no users, ValkyrieID serves a setup wizard at `/setup` to create the first administrator.

::: warning Finish setup right away
Until the first user exists, anyone who can reach `/setup` can create the administrator account. Complete setup before exposing the server, or create the first admin with the CLI instead (see below).
:::

1. Start the server (see [Getting started](/guide/getting-started)).
2. Open `https://<your issuer>/setup`.
3. Enter a username, an optional email address, and a password that meets the [password policy](/reference/configuration#passwords).
4. Select **Create administrator**.

You are signed in and redirected to `/account`. From then on, `/setup` returns `404`. The wizard can't be run again, even if every user is later disabled.

## Without the wizard

You can also create the first admin from the command line. The wizard then never appears:

```sh
./valkyrieid user create admin --email admin@example.com
./valkyrieid admin grant-admin admin
```

See the [CLI reference](/reference/cli#users).

## Signing in

Users sign in at `/account/login` with their username or email address. After too many failed attempts the account is [locked](/reference/configuration#lockout) for a while. Failed sign-ins always show the same message, so the page doesn't reveal which accounts exist.

If an admin is locked out or has forgotten their password, use the [admin recovery commands](/reference/cli#admin-recovery).
