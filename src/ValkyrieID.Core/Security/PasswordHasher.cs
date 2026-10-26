using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ValkyrieID.Core.Security;

public enum PasswordVerificationResult
{
    Failed,
    Success,

    /// <summary>The password is correct but was hashed with different parameters; store a new hash.</summary>
    SuccessRehashNeeded,
}

/// <summary>
/// Argon2id cost parameters. The default is one of the equivalent configurations recommended by the OWASP Password
/// Storage Cheat Sheet (m=12 MiB, t=3, p=1); it trades some CPU for less memory than m=19 MiB, t=2.
/// </summary>
public sealed record Argon2Parameters(int MemorySizeKiB = 12288, int Iterations = 3, int Parallelism = 1)
{
    public static Argon2Parameters Default { get; } = new();
}

/// <summary>
/// Hashes passwords with Argon2id and stores them as PHC strings:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c> (unpadded base64).
/// </summary>
public sealed class PasswordHasher(Argon2Parameters parameters)
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Version = 0x13;

    // A hash that is verified when a user doesn't exist, so the response time doesn't reveal it.
    private readonly Lazy<string> _dummyHash = new(() => new PasswordHasher(parameters).Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public PasswordHasher()
        : this(Argon2Parameters.Default)
    {
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, parameters, HashSize);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"$argon2id$v={Version}$m={parameters.MemorySizeKiB},t={parameters.Iterations},p={parameters.Parallelism}${Encode(salt)}${Encode(hash)}");
    }

    public PasswordVerificationResult Verify(string password, string? passwordHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (passwordHash is null || !TryParse(passwordHash, out var stored, out var salt, out var expected))
        {
            VerifyDummy(password);
            return PasswordVerificationResult.Failed;
        }

        var actual = Compute(password.Length == 0 ? "\0" : password, salt, stored, expected.Length);
        if (password.Length == 0 || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        return stored == parameters && expected.Length == HashSize
            ? PasswordVerificationResult.Success
            : PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Spends the same time as verifying a real password; used for unknown users.</summary>
    public void VerifyDummy(string password) => Verify(password, _dummyHash.Value);

    private static byte[] Compute(string password, byte[] salt, Argon2Parameters parameters, int length)
    {
        // NIST SP 800-63B §5.1.1.2: passwords SHOULD be Unicode-normalized before hashing.
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try
        {
            using var argon2 = new Argon2id(bytes)
            {
                Salt = salt,
                MemorySize = parameters.MemorySizeKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon2.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(string value, out Argon2Parameters parameters, out byte[] salt, out byte[] hash)
    {
        parameters = Argon2Parameters.Default;
        salt = hash = [];

        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        var parts = value.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != $"v={Version}")
        {
            return false;
        }

        int? memory = null, iterations = null, parallelism = null;
        foreach (var pair in parts[3].Split(','))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 || !int.TryParse(pair.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            {
                return false;
            }

            switch (pair[..separator])
            {
                case "m": memory = number; break;
                case "t": iterations = number; break;
                case "p": parallelism = number; break;
                default: return false;
            }
        }

        // Bound stored parameters so a corrupted or malicious hash can't exhaust memory or CPU.
        if (memory is not (>= 8 and <= 1024 * 1024) || iterations is not (>= 1 and <= 100) || parallelism is not (>= 1 and <= 64)
            || !TryDecode(parts[4], out salt) || salt.Length < 8
            || !TryDecode(parts[5], out hash) || hash.Length is < 16 or > 64)
        {
            return false;
        }

        parameters = new Argon2Parameters(memory.Value, iterations.Value, parallelism.Value);
        return true;
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        var padded = value + new string('=', (4 - (value.Length % 4)) % 4);
        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
