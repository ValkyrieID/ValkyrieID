using System.Security.Cryptography;
using System.Text;

namespace ValkyrieID.Core.Security;

/// <summary>
/// Encrypts secrets at rest (signing keys, TOTP secrets, SMTP password) with the master key using AES-256-GCM.
/// </summary>
/// <remarks>
/// Format: <c>version (1) | keyId (4) | nonce (12) | ciphertext | tag (16)</c>.
/// The version, key id, and caller-supplied purpose are bound as associated data, so a ciphertext
/// cannot be reused for a different purpose. Previous keys are only used for decryption, which
/// allows master key rotation by re-encrypting with <see cref="Protect"/>.
/// </remarks>
public sealed class SecretProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int AadHeaderSize = 1 + MasterKey.KeyIdSizeBytes;
    private const int HeaderSize = AadHeaderSize + NonceSize;

    private readonly MasterKey _current;
    private readonly MasterKey[] _keys;

    public SecretProtector(MasterKey current, IEnumerable<MasterKey>? previous = null)
    {
        _current = current;
        _keys = [current, .. (previous ?? []).Where(k => !k.Id.AsSpan().SequenceEqual(_current.Id))];
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        var output = new byte[HeaderSize + plaintext.Length + TagSize];
        output[0] = FormatVersion;
        _current.Id.CopyTo(output, 1);

        var nonce = output.AsSpan(AadHeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_current.Key, TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(HeaderSize, plaintext.Length),
            output.AsSpan(HeaderSize + plaintext.Length, TagSize),
            BuildAad(output.AsSpan(0, AadHeaderSize), purpose));

        return output;
    }

    public byte[] Protect(string plaintext, string purpose) => Protect(Encoding.UTF8.GetBytes(plaintext), purpose);

    /// <exception cref="CryptographicException">The data is malformed, tampered with, has a different purpose, or was encrypted with an unknown key.</exception>
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        if (protectedData.Length < HeaderSize + TagSize || protectedData[0] != FormatVersion)
        {
            throw InvalidData();
        }

        var key = FindKey(protectedData.Slice(1, MasterKey.KeyIdSizeBytes)) ?? throw InvalidData();
        var ciphertextLength = protectedData.Length - HeaderSize - TagSize;
        var plaintext = new byte[ciphertextLength];

        try
        {
            using var aes = new AesGcm(key.Key, TagSize);
            aes.Decrypt(
                protectedData.Slice(AadHeaderSize, NonceSize),
                protectedData.Slice(HeaderSize, ciphertextLength),
                protectedData.Slice(HeaderSize + ciphertextLength, TagSize),
                plaintext,
                BuildAad(protectedData[..AadHeaderSize], purpose));
        }
        catch (CryptographicException)
        {
            throw InvalidData();
        }

        return plaintext;
    }

    public string UnprotectString(ReadOnlySpan<byte> protectedData, string purpose) =>
        Encoding.UTF8.GetString(Unprotect(protectedData, purpose));

    /// <summary>Whether the data was encrypted with a key other than the current one and should be re-encrypted.</summary>
    public bool RequiresReencryption(ReadOnlySpan<byte> protectedData) =>
        protectedData.Length < AadHeaderSize
        || !protectedData.Slice(1, MasterKey.KeyIdSizeBytes).SequenceEqual(_current.Id);

    private MasterKey? FindKey(ReadOnlySpan<byte> keyId)
    {
        foreach (var key in _keys)
        {
            if (keyId.SequenceEqual(key.Id))
            {
                return key;
            }
        }

        return null;
    }

    private static byte[] BuildAad(ReadOnlySpan<byte> header, string purpose)
    {
        var aad = new byte[header.Length + Encoding.UTF8.GetByteCount(purpose)];
        header.CopyTo(aad);
        Encoding.UTF8.GetBytes(purpose, aad.AsSpan(header.Length));
        return aad;
    }

    private static CryptographicException InvalidData() =>
        new("The protected data is invalid, was tampered with, or was encrypted with an unknown master key.");
}
