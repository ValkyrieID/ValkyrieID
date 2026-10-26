using System.Buffers.Text;
using System.Security.Cryptography;

namespace ValkyrieID.Core.Users;

/// <summary>A server-side SSO session, referenced by the session cookie. Its id becomes the <c>sid</c> claim.</summary>
public sealed class UserSession
{
    public string Id { get; init; } = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    public Guid UserId { get; init; }

    /// <summary>When the user authenticated (<c>auth_time</c>).</summary>
    public required DateTimeOffset AuthenticatedAt { get; init; }

    /// <summary>Authentication methods used (<c>amr</c>, RFC 8176), e.g. <c>pwd</c>.</summary>
    public List<string> AuthenticationMethods { get; init; } = [];

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}
