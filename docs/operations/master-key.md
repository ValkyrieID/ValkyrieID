# Master encryption key

ValkyrieID encrypts secrets stored in the database with a master key:

- ASP.NET Data Protection keys (used for cookies and anti-forgery tokens)
- signing keys, TOTP secrets, and the SMTP password (in later releases)

Secrets are encrypted with AES-256-GCM. The master key is never stored in the database.

::: danger Back up the master key
A database backup is useless without the master key that encrypted it. Store the key in a secret manager and back it up together with, but separately from, the database.
:::

## Generate a key

The key is 32 random bytes, base64 encoded:

```sh
openssl rand -base64 32
```

PowerShell 7+:

```powershell
$b = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($b); [Convert]::ToBase64String($b)
```

Supply it as `MasterKey__Current` (environment variable) or `MasterKey:Current` (configuration). Prefer environment variables or a secret store over committing it to `appsettings.json`.

## Rotate the key

1. Generate a new key.
2. Set the new key as `MasterKey:Current` and move the old key to `MasterKey:Previous:0`.
3. Restart all instances.

New secrets are encrypted with the current key; existing secrets can still be decrypted with the previous key. Data Protection keys are replaced automatically every 90 days, so keep the previous master key configured for at least 90 days plus your longest session lifetime before removing it.
