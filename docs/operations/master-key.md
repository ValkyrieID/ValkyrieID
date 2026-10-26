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
4. Re-encrypt the stored secrets with the new key:

   ```sh
   ./valkyrieid admin reencrypt
   ```

5. Confirm nothing uses the old key any more:

   ```sh
   ./valkyrieid admin reencrypt --check   # exit code 0 when done
   ```

6. Remove `MasterKey:Previous:0` and restart all instances.

New secrets are encrypted with the current key, and existing secrets can still be decrypted with a previous key until step 4 has run. `admin reencrypt` runs in a single database transaction and is safe to repeat. It fails, without changing anything, if some data was encrypted with a key that isn't configured.

Run step 4 after every instance uses the new key (step 3), so no instance writes new secrets with the old key in the meantime.
