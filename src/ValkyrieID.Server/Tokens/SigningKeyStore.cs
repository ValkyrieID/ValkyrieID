using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Tokens;

internal sealed record LoadedSigningKey(SigningKey Key, RsaSecurityKey SecurityKey);

internal sealed record SigningKeySet(IReadOnlyList<LoadedSigningKey> Keys, DateTimeOffset LoadedAt)
{
    /// <summary>The newest key that has activated; <see langword="null"/> if there is none.</summary>
    public LoadedSigningKey? Current(DateTimeOffset now) =>
        Keys.Where(k => k.Key.ActivatesAt <= now).MaxBy(k => k.Key.ActivatesAt);
}

/// <summary>Caches the signing keys from the database and reloads them periodically.</summary>
internal sealed class SigningKeyStore(
    IServiceScopeFactory scopeFactory,
    SecretProtector protector,
    TimeProvider timeProvider,
    IOptions<SigningKeyOptions> options) : IDisposable
{
    public const string PrivateKeyPurpose = "ValkyrieID.SigningKey";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private SigningKeySet? _keys;

    public async Task<SigningKeySet> GetAsync(CancellationToken cancellationToken)
    {
        var keys = _keys;
        if (keys is not null && timeProvider.GetUtcNow() - keys.LoadedAt < options.Value.CheckInterval)
        {
            return keys;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            keys = _keys;
            if (keys is not null && timeProvider.GetUtcNow() - keys.LoadedAt < options.Value.CheckInterval)
            {
                return keys;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ValkyrieDbContext>();
            var entities = await db.SigningKeys.AsNoTracking().ToListAsync(cancellationToken);
            _keys = keys = new SigningKeySet([.. entities.Select(Load)], timeProvider.GetUtcNow());
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _keys = null;

    public void Dispose() => _lock.Dispose();

    private LoadedSigningKey Load(SigningKey key)
    {
        var pkcs8 = protector.Unprotect(key.EncryptedPrivateKey, PrivateKeyPurpose);
        try
        {
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return new LoadedSigningKey(key, new RsaSecurityKey(rsa) { KeyId = key.Id });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    public static SigningKey Create(SecretProtector protector, int keySize, DateTimeOffset now, DateTimeOffset activatesAt)
    {
        using var rsa = RSA.Create(keySize);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        try
        {
            return new SigningKey
            {
                Id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
                Algorithm = SecurityAlgorithms.RsaSha256,
                PublicKey = rsa.ExportSubjectPublicKeyInfo(),
                EncryptedPrivateKey = protector.Protect(pkcs8, PrivateKeyPurpose),
                CreatedAt = now,
                ActivatesAt = activatesAt,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
