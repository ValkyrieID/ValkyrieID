using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace ValkyrieID.Core.Security;

/// <summary>
/// A 256-bit master encryption key used to protect secrets at rest.
/// </summary>
public sealed class MasterKey
{
    public const int KeySizeBytes = 32;
    public const int KeyIdSizeBytes = 4;

    private MasterKey(byte[] key)
    {
        Key = key;
        Id = SHA256.HashData(key)[..KeyIdSizeBytes];
    }

    /// <summary>Short, non-secret identifier derived from the key, stored alongside ciphertexts.</summary>
    public byte[] Id { get; }

    internal byte[] Key { get; }

    public static MasterKey Generate() => new(RandomNumberGenerator.GetBytes(KeySizeBytes));

    public static MasterKey Parse(string base64) =>
        TryParse(base64, out var key, out var error) ? key : throw new FormatException(error);

    public static bool TryParse(
        string? base64,
        [NotNullWhen(true)] out MasterKey? key,
        [NotNullWhen(false)] out string? error)
    {
        key = null;

        if (string.IsNullOrWhiteSpace(base64))
        {
            error = "The master key is empty.";
            return false;
        }

        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written))
        {
            error = "The master key is not valid base64.";
            return false;
        }

        if (written != KeySizeBytes)
        {
            CryptographicOperations.ZeroMemory(buffer);
            error = $"The master key must be exactly {KeySizeBytes} bytes ({KeySizeBytes * 8} bits); got {written} bytes.";
            return false;
        }

        key = new MasterKey(buffer[..KeySizeBytes]);
        CryptographicOperations.ZeroMemory(buffer);
        error = null;
        return true;
    }

    public string ToBase64() => Convert.ToBase64String(Key);
}
