# Admin UI

ValkyrieID includes a web interface for administrators at `/admin`, for example `https://id.example.com/admin/`. It manages everything the [Admin API](/reference/admin-api) covers:

- users: profile, password, enable and disable, lockout, 2FA, verification email, roles, groups, custom claims, and sessions
- roles and groups
- clients and their secrets
- API resources, their scopes, and introspection secrets
- signing keys (status and manual rotation)
- the audit log, with filters
- [runtime settings](/reference/configuration#runtime-settings): token and session lifetimes, password policy, lockout, 2FA requirements, email verification, SMTP (with a test email), branding, and audit log retention

Built-in entries (the admin UI client and the Admin API resource) are shown read-only. So are settings set in the server's configuration, which takes precedence.

## Signing in

Only users with the built-in `admin` role can use the admin UI. Grant it with the [setup wizard](/guide/first-run), another admin, or the CLI:

```sh
valkyrieid admin grant-admin alice
```

The admin UI signs in through ValkyrieID itself, like any other app, with the built-in public client `valkyrieid-admin-ui` (authorization code flow with PKCE). If you are already signed in to ValkyrieID, it opens without asking again.

By default, admins must sign in with [two-factor authentication](/guide/two-factor); admins who haven't set it up are asked to at sign-in. If your current session was signed in with a password only, the admin UI asks you to sign in again.

- Access tokens are kept in memory only and last 10 minutes. There are no refresh tokens: when a token is about to expire, or you reload the page, the admin UI gets a new one through your ValkyrieID session, without showing a sign-in page. If the session has ended, you sign in again.
- Removing the `admin` role or disabling the account takes effect on the next request, because the Admin API checks the user every time.
- **Sign out** ends your ValkyrieID session (not just the admin UI) and notifies the other apps you signed in to.

## Security

- The admin UI is served by the same server, on the same origin as the Admin API. The API sends no CORS headers, so no other website can call it.
- Its pages have their own Content Security Policy: scripts, styles, and API calls are only allowed from the server itself, with no inline scripts or styles, and the pages can't be framed.
- The access token is only sent to `/api/admin` and never stored in the browser.

## Turning it off

If you manage ValkyrieID with the CLI or your own scripts only, turn the admin UI off with `AdminUI__Enabled=false`, or the whole Admin API with `AdminApi__Enabled=false`. To keep it internal instead, see [Reverse proxy](/operations/reverse-proxy#keeping-administration-internal).

## Building from source

The admin UI is an Angular app in `src/ValkyrieID.AdminUI`. `dotnet publish` builds it (with Node.js and npm) and includes it in the output; pass `-p:SkipAdminUI=true` to skip it.

During development, build it into the server's `wwwroot/admin` and run the server:

```sh
cd src/ValkyrieID.AdminUI
npm ci
npm run watch          # rebuilds on changes
dotnet run --project ../ValkyrieID.Server
```

It must be opened through the server (at `<issuer>/admin/`), because the client's redirect URI is derived from the issuer URL.
