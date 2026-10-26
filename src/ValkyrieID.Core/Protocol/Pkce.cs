using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ValkyrieID.Core.Protocol;

/// <summary>Proof Key for Code Exchange (RFC 7636), <c>S256</c> only.</summary>
public static class Pkce
{
    public const string S256 = "S256";

    /// <summary>RFC 7636 A4.1: <c>code-verifier = 43*128unreserved</c>.</summary>
    public static bool IsValidVerifier(string value) => value.Length is >= 43 and <= 128 && value.All(IsUnreserved);

    /// <summary>An S256 challenge is the base64url encoding of a SHA-256 hash: 43 characters without padding.</summary>
    public static bool IsValidS256Challenge(string value) =>
        value.Length == 43 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>RFC 7636 A4.6: <c>BASE64URL-ENCODE(SHA256(ASCII(code_verifier))) == code_challenge</c>, compared in constant time.</summary>
    public static bool Verify(string verifier, string challenge)
    {
        var computed = Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        return CryptographicOperations.FixedTimeEquals(computed, Encoding.ASCII.GetBytes(challenge));
    }

    private static bool IsUnreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
