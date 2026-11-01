using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ValkyrieID.Core.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238) with the parameters authenticator apps support: HMAC-SHA1, 6 digits,
/// 30-second steps.
/// </summary>
public static class Totp
{
    public const int Digits = 6;

    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Time steps accepted on either side of the current one, for clock drift (RFC 6238 §5.2; PROJECT.md §6: ±1).
    /// </summary>
    public const int AllowedDrift = 1;

    /// <summary>160-bit secrets, the HMAC-SHA1 output length recommended by RFC 4226 §4 (R6).</summary>
    public const int SecretSizeBytes = 20;

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretSizeBytes);

    public static long TimeStep(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>The code for <paramref name="timeStep"/> (RFC 4226 §5.3 dynamic truncation).</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 and every authenticator app use; HMAC-SHA1 isn't affected by SHA-1 collisions.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7fffffff;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the matching time step within the allowed drift that is later than <paramref name="lastUsedStep"/>,
    /// or <see langword="null"/>. Codes at or before the last used step are rejected, so a code can't be used
    /// twice (RFC 6238 §5.2: the verifier MUST NOT accept the second attempt of the same OTP). Every candidate is
    /// compared in constant time.
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, long? lastUsedStep)
    {
        var normalized = Normalize(code);
        var candidate = Encoding.ASCII.GetBytes(normalized ?? "------");
        var current = TimeStep(now);
        long? match = null;
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, step)), candidate);
            if (equal && normalized is not null && step > (lastUsedStep ?? long.MinValue) && match is null)
            {
                match = step;
            }
        }

        return match;
    }

    /// <summary>The key-URI format authenticator apps read from QR codes.</summary>
    public static string KeyUri(ReadOnlySpan<byte> secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}";

    /// <summary>Digits only, spaces allowed (apps display "123 456"); anything else is no code.</summary>
    private static string? Normalize(string code)
    {
        if (code.Length > 16)
        {
            return null;
        }

        var digits = code.Replace(" ", string.Empty, StringComparison.Ordinal);
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }
}

/// <summary>RFC 4648 §6 base32 without padding, as used by TOTP key URIs.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    /// <exception cref="FormatException">The input contains characters outside the base32 alphabet.</exception>
    public static byte[] Decode(string text)
    {
        var input = text.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in input)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Invalid base32 character.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
