using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Security;

internal sealed record ReencryptionReport(int SigningKeys, int DataProtectionKeys)
{
    public int Total => SigningKeys + DataProtectionKeys;
}

/// <summary>
/// Re-encrypts everything stored under a previous master key with the current one, so the previous key can be
/// removed from configuration. Running it again changes nothing.
/// </summary>
internal sealed class MasterKeyReencryptor(ValkyrieDbContext db, SecretProtector protector)
{
    /// <param name="apply">When <see langword="false"/>, only counts what would be re-encrypted.</param>
    public async Task<ReencryptionReport> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        await using var transaction = apply ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

        var signingKeys = 0;
        foreach (var key in await db.SigningKeys.ToListAsync(cancellationToken))
        {
            if (protector.RequiresReencryption(key.EncryptedPrivateKey))
            {
                signingKeys++;
                key.EncryptedPrivateKey = Reencrypt(key.EncryptedPrivateKey, SigningKeyStore.PrivateKeyPurpose);
            }
        }

        var dataProtectionKeys = 0;
        foreach (var key in await db.DataProtectionKeys.ToListAsync(cancellationToken))
        {
            if (key.Xml is null)
            {
                continue;
            }

            var element = XElement.Parse(key.Xml);
            var changed = false;
            foreach (var value in element.Descendants().Where(e => e.Name.LocalName == "encryptedKey").Elements().Where(e => e.Name.LocalName == "value"))
            {
                var ciphertext = Convert.FromBase64String(value.Value);
                if (protector.RequiresReencryption(ciphertext))
                {
                    value.Value = Convert.ToBase64String(Reencrypt(ciphertext, MasterKeyXmlEncryptor.Purpose));
                    changed = true;
                }
            }

            if (changed)
            {
                dataProtectionKeys++;
                key.Xml = element.ToString(SaveOptions.DisableFormatting);
            }
        }

        if (transaction is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            db.ChangeTracker.Clear();
        }

        return new ReencryptionReport(signingKeys, dataProtectionKeys);
    }

    private byte[] Reencrypt(byte[] ciphertext, string purpose)
    {
        var plaintext = protector.Unprotect(ciphertext, purpose);
        try
        {
            return protector.Protect(plaintext, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
