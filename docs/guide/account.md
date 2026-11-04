# Your account

Signed-in users manage their own account at `/account`. All account pages are server-rendered and work without JavaScript.

## Re-entering your password

Changing the password, changing the email address, managing two-factor authentication, and getting new recovery codes all ask for the current password, every time. Someone who gets hold of a signed-in browser can't use it to take over the account.

Wrong passwords on these forms count towards the [account lockout](/reference/configuration#lockout), like wrong passwords at sign-in. While the account is locked, these forms are refused even with the right password. The current session stays signed in. Form submissions are [rate limited](/reference/configuration#rate-limits) with the `TwoFactor` rule.

## Profile

`/account/profile` changes the display name, given name, and family name. These are returned in the `name`, `given_name`, and `family_name` claims (`profile` scope). The email address is changed on /account/email when [email is set up](/guide/email#email-change); otherwise, ask an administrator.

## Email address

`/account/email` shows the address and whether it's verified, sends a verification link, and changes the address with confirmation from the new one. It's only available when SMTP is configured; see [Email](/guide/email).

## Change password

`/account/password` asks for the current password and the new one twice. The new password must meet the [password policy](/reference/configuration#passwords) and differ from the current one.

After the change:

- this browser stays signed in;
- all other sessions are signed out, and their applications are notified by [back-channel logout](/reference/endpoints#back-channel-logout);
- **all** of the user's refresh tokens are revoked, including those issued to applications in this session. Applications that keep the user signed in with `offline_access` need a new sign-in;
- if email is set up, the account's address gets a notice.

## Two-factor authentication

`/account/two-factor` turns [two-factor authentication](/guide/two-factor) on and off and replaces recovery codes.

- **New recovery codes** require the password. All previous codes, used or not, stop working, and the new ones are shown once.
- **Turning 2FA off** requires the password and a current code from the authenticator app or an unused recovery code. Like a password change, it signs out all other sessions; this browser stays signed in. It's not possible while 2FA is required, by `TwoFactor:Required`, `user require-2fa`, or because the user is an admin and `AdminApi:RequireTwoFactor` is on.

## Sessions

`/account/sessions` lists the browsers and devices where the user is signed in: the browser's user agent, IP address, sign-in time, and last activity. The current session is marked.

- **Sign out this session** ends that session, notifies its applications by back-channel logout, and revokes the refresh tokens issued from it. Choosing the current session signs this browser out.
- **Sign out all other sessions** does the same for every session except the current one.

Users can only see and sign out their own sessions.

## Audit log

Every change is written to the audit log with the user's ID as the actor:

| Event | When |
| --- | --- |
| `user.profile_updated` | Names changed; the details list the changed fields, not their values. |
| `user.password_changed` | Password changed. |
| `user.2fa.enabled` / `user.2fa.disabled` | 2FA turned on or off. |
| `user.2fa.recovery_codes_regenerated` | New recovery codes. |
| `session.revoked` | A session was signed out; `reason` is `user_revoked`, `user_revoked_others`, `password_changed`, or `2fa_disabled`. |
| `user.email_change.requested` / `user.email_changed` | See [Email](/guide/email#audit-log). |
| `user.login.failed` / `user.locked_out` | Wrong password or code on an account form, or the account locked. |
