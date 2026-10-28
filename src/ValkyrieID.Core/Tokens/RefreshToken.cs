namespace ValkyrieID.Core.Tokens;

/// <summary>
/// An opaque refresh token. Only its SHA-256 hash is stored. Every use replaces the token with a new one in the same
/// family; presenting a replaced token again revokes the whole family (RFC 9700 §4.14.2).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>SHA-256 hash of the token. The plaintext is never stored.</summary>
    public required byte[] Hash { get; init; }

    /// <summary>All tokens descending from one authorization code share a family.</summary>
    public Guid FamilyId { get; init; }

    /// <summary>The OAuth <c>client_id</c> the token was issued to.</summary>
    public required string ClientId { get; init; }

    public Guid UserId { get; init; }

    /// <summary>The SSO session (<c>sid</c>) the family was issued from.</summary>
    public string? SessionId { get; init; }

    /// <summary>When the user originally authenticated (<c>auth_time</c>).</summary>
    public required DateTimeOffset AuthTime { get; init; }

    public List<string> AuthenticationMethods { get; init; } = [];

    /// <summary>The scope granted with the original authorization; it never grows (RFC 6749 §6).</summary>
    public List<string> Scopes { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Sliding expiry of this token; never later than <see cref="FamilyExpiresAt"/>.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Absolute expiry of the family, fixed when the first token was issued.</summary>
    public required DateTimeOffset FamilyExpiresAt { get; init; }

    /// <summary>Set when the token was exchanged for a new one.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public Guid? ReplacedById { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) =>
        ConsumedAt is null && RevokedAt is null && now < ExpiresAt && now < FamilyExpiresAt;
}
