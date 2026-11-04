# Email

When an SMTP server is configured, ValkyrieID sends emails for:

- **password reset**: "Forgot your password?" on the sign-in page;
- **email verification**: confirms that a user's address belongs to them, so `email_verified` is `true` in tokens;
- **email change**: users change their address on `/account/email`, confirmed from the new address;
- **security notices**: after a password change or reset, and to the old address after an email change.

Without SMTP these pages return `404`, and only an administrator can change a user's email address.

## Configure SMTP

```sh
Smtp__Host=smtp.example.com
Smtp__Port=587
Smtp__Security=StartTls
Smtp__UserName=valkyrieid
Smtp__Password=...
Smtp__FromAddress=id@example.com
```

See [SMTP](/reference/configuration#smtp) for all settings. The server certificate is always validated. Instead of configuration, you can also enter the settings in the admin UI (**Settings → SMTP**); the password is then stored encrypted with the [master key](/operations/master-key). Settings in configuration take precedence.

Then send a test email, with **Send test email** on the admin UI's SMTP page (to your own address), or with the CLI:

```sh
./valkyrieid admin send-test-email you@example.com
```

The command sends right away and prints the SMTP error if it fails. The admin UI only shows a short reason, such as `authentication_failed`.

## How emails are sent

Emails are queued in the database and sent by a background worker, never during the request. A request that sends an email therefore takes as long as one that doesn't, and the response time doesn't reveal whether an account exists. Queued emails are encrypted with the [master key](/operations/master-key) and deleted once sent. Failed sends are retried after network errors, timeouts, and `4xx` SMTP replies. A `5xx` reply (for example, an unknown mailbox) isn't retried. With several instances, each email is sent by only one of them. Emails queued by the CLI are sent by the running server within `Smtp:PollInterval`.

The audit log records `email.sent` and `email.failed` with the kind of email, but not the recipient.

## Links

Links in emails:

- are built from the configured [issuer URL](/reference/configuration#issuer), never from the request's `Host` header, so a forged request can't send users a link to another site;
- contain a random 256-bit token. Only its SHA-256 hash is stored;
- work **once**, and only until they expire (`Email:PasswordResetLifetime`, default 30 minutes; `Email:VerificationLifetime`, default 24 hours);
- stop working when a newer link of the same kind is sent, and when the password changes;
- only show a page when opened. The token is used when the user submits that page, so email scanners that open links can't use them up.

At most one email of each kind is sent to an account per `Email:ResendInterval` (default 1 minute). Further requests are ignored silently. The forms are also [rate limited](/reference/configuration#rate-limits) per IP address with the `Email` rule.

## Password reset

`/account/forgot-password` asks for a username or email address. The answer is always the same, "If an account with an email address exists ...", whether the account exists or not. A link is only sent to enabled accounts that have an email address. The address doesn't need to be verified, since only an administrator or a confirmed email change can set it.

On `/account/reset-password` the user chooses a new password that meets the [password policy](/reference/configuration#passwords). After the reset:

- **all** sessions are signed out, including the one in this browser, and their applications are notified by [back-channel logout](/reference/endpoints#back-channel-logout);
- all of the user's refresh tokens are revoked;
- an active [lockout](/reference/configuration#lockout) is lifted, since the user has proven control of the mailbox;
- two-factor authentication still applies at the next sign-in. A reset never turns it off;
- the account's address gets a notice.

## Email verification

On `/account/email`, users with an unverified address can send themselves a verification link. Opening it (`/account/verify-email`) and confirming marks the address as verified. Signing in isn't required. A link only verifies the address it was sent to.

### Verification at sign-in

[`Email:VerificationAtSignIn`](/reference/configuration#email-links) asks users to verify their address when they sign in to an application:

- `Off` (default): users verify from their account page, if at all.
- `Prompt`: after sign-in, users with an unverified address see a page that offers a verification link, and can continue to the application. It is shown once per sign-in.
- `Require`: users with an unverified address (or none) can't continue to applications until it's verified. The page offers a verification link, and a link to add an address. Once the address is verified, in this browser or another one, **Continue** returns to the application. Applications that sign in silently (`prompt=none`) get `interaction_required`.

The setting only applies while SMTP is configured: without it, addresses can't be verified, so it behaves as `Off`. The admin UI and Admin API refuse `Prompt` and `Require` without SMTP.

## Email change

On `/account/email`, users enter the new address and their current password. As with the other [sensitive account forms](/guide/account#re-entering-your-password), wrong passwords count towards the lockout.

- A link is sent to the **new** address. The address changes only when the link is opened and confirmed (`/account/confirm-email`). Until then, the old address stays in use for sign-in and password resets.
- If another account already uses the new address, it gets a notice without a link instead. The page shows the same message either way, so it doesn't reveal which addresses are registered.
- After the change, the new address is verified, all other pending links (including password reset links sent to the old address) stop working, and the old address gets a notice with the new address partly hidden.
- Sessions stay signed in.

## Audit log

| Event | When |
| --- | --- |
| `user.password_reset.requested` | A reset was requested. `success` is `false`, with a `reason`, when no email was sent: `unknown_user`, `disabled`, `no_email`, or `throttled`. |
| `user.password_reset.completed` | The password was reset with a link. |
| `user.email_verification.requested` / `user.email_verified` | A verification link was sent / used. |
| `user.email_change.requested` / `user.email_changed` | A change was requested (`address_in_use` tells whether a notice was sent instead of a link) / confirmed. |
| `user.password_reset.failed`, `user.email_verification.failed`, `user.email_change.failed` | An invalid, used, or expired link was submitted. |
| `session.revoked` | With `reason` `password_reset`, for every session signed out by a reset. |
| `email.sent` / `email.failed` | An email was sent, or given up on. |
| `email.test` | `admin send-test-email` was run, or **Send test email** in the admin UI. |
