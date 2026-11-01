# Two-factor authentication

Users can protect their account with a time-based one-time password (TOTP, RFC 6238) from an authenticator app, such as Google Authenticator, Microsoft Authenticator, or 1Password, in addition to their password.

## Set it up

1. Sign in and open **Your account → Two-factor authentication** (`/account/two-factor`).
2. Scan the QR code with the app, or enter the key shown below it.
3. Enter the 6-digit code from the app and your current password.
4. Save the 10 recovery codes that are shown. They aren't shown again.

The password is asked for so that someone who got hold of a signed-in browser can't add their own authenticator and lock the real user out.

## Sign in

After the password, the user is asked for a code from the app. The session is only created once the code is correct; until then, nothing else is accessible. The step expires after 5 minutes, and the user starts over.

- Codes from the previous and next 30-second step are accepted, to allow for clock drift.
- Each code works only once, so an intercepted code can't be replayed.
- Wrong codes count towards the [account lockout](/reference/configuration#lockout), and code submissions are [rate limited](/reference/configuration#rate-limits).

ID tokens report the sign-in methods in `amr` (RFC 8176): `pwd otp mfa` after a code, or `pwd mfa` after a recovery code.

## Recovery codes

If the authenticator isn't available, the user chooses **Use a recovery code instead** and enters one of their recovery codes. Each code works once. Codes are stored as hashes, so they can't be shown again; the account's 2FA page shows how many are left.

A user who has lost both the authenticator and the recovery codes needs an administrator to run [`admin reset-2fa`](/reference/cli#admin-recovery). This turns 2FA off and signs the user out everywhere.

## Require 2FA

- For everyone: set [`TwoFactor:Required`](/reference/configuration#two-factor-authentication) to `true`.
- For one user: [`user require-2fa <user>`](/reference/cli#users).

A user who must use 2FA but hasn't set it up is taken through the setup after entering their password, and only gets a session once it's done. Existing sessions continue until they end.

::: warning Setup at sign-in trusts the password
When 2FA is required and a user hasn't set it up yet, whoever signs in with the password first sets up the authenticator. Ask users to set up 2FA from their account page before you turn on the requirement, or require it before handing out initial passwords.
:::

## Storage

TOTP secrets are encrypted with the [master key](/operations/master-key) and are re-encrypted by `admin reencrypt`. Recovery codes are stored as SHA-256 hashes; each has 80 random bits.
