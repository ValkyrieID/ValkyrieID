# Signing key rotation

ValkyrieID signs tokens with RSA keys (`RS256`) that it generates and rotates itself. Private keys are stored in the database, encrypted with the [master key](/operations/master-key).

## Lifecycle

1. **First start:** if there is no usable key, one is created and used immediately.
2. **Published:** `SigningKeys:PublishLead` (default 2 days) before the current key's `RotationInterval` (default 90 days) ends, the next key is created and published in JWKS. It isn't used yet, so resource servers can pick it up first.
3. **Active:** when its activation time arrives, the new key signs all new tokens.
4. **Retired:** the previous key stays in JWKS until every token it signed has expired (the longest access token lifetime, including client overrides, plus clock skew).
5. **Removed:** the retired key is deleted.

Rotation runs every `SigningKeys:CheckInterval` (default 5 minutes). Key creation and deletion are written to the audit log.

## Resource servers

Resource servers must fetch keys from the JWKS endpoint and select the key by the token's `kid`. Don't pin a single key. Make sure your JWKS cache refreshes more often than `SigningKeys:PublishLead`; ASP.NET Core `AddJwtBearer` does this by default.

## Multiple instances

All instances share the keys in the database and reload them every `CheckInterval`. `PublishLead` must be at least `CheckInterval`, so every instance sees a new key before it is used.
