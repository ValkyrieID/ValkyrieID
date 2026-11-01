using System.Security.Cryptography;
using System.Text;

namespace ValkyrieID.Core.Security;

/// <summary>
/// One-time recovery codes for when the authenticator is unavailable. Each code has 80 random bits, so a fast
/// hash (SHA-256) is enough to store them.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 characters, without 0/O and 1/I.
    private const int Length = 16;

    /// <summary>A new code formatted as four groups of four characters, e.g. <c>ABCD-EFGH-JKLM-NPQR</c>.</summary>
    public static string Generate()
    {
        var code = new StringBuilder(Length + 3);
        for (var i = 0; i < Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                code.Append('-');
            }

            code.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return code.ToString();
    }

    /// <summary>Hash of the code ignoring case, spaces, and dashes; <see langword="null"/> if it can't be a code.</summary>
    public static byte[]? Hash(string code)
    {
        if (code.Length > 64)
        {
            return null;
        }

        var normalized = new string([.. code.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)]);
        return normalized.Length == Length && normalized.All(c => Alphabet.Contains(c, StringComparison.Ordinal))
            ? SHA256.HashData(Encoding.ASCII.GetBytes(normalized))
            : null;
    }
}
