namespace ValkyrieID.Core.Tokens;

/// <summary>
/// An RSA token signing key. A key is published in JWKS from creation, signs tokens from
/// <see cref="ActivatesAt"/> until a newer key activates, and stays published until the tokens it
/// signed have expired.
/// </summary>
public sealed class SigningKey
{
    /// <summary>The JWK <c>kid</c>.</summary>
    public required string Id { get; init; }

    /// <summary>JWS algorithm, e.g. <c>RS256</c>.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Public key as DER-encoded SubjectPublicKeyInfo.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>PKCS#8 private key, encrypted with the master key.</summary>
    public required byte[] EncryptedPrivateKey { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ActivatesAt { get; init; }
}
