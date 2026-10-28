namespace ValkyrieID.Core.Clients;

public enum ClientType
{
    Confidential = 0,
    Public = 1,
}

public sealed class Client
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The OAuth <c>client_id</c>. Unique and immutable.</summary>
    public required string ClientId { get; init; }

    public string? DisplayName { get; set; }

    public ClientType Type { get; init; } = ClientType.Confidential;

    /// <summary>Disabled clients can't authenticate or obtain tokens.</summary>
    public bool Enabled { get; set; } = true;

    public List<string> AllowedGrantTypes { get; set; } = [];

    public List<string> AllowedScopes { get; set; } = [];

    /// <summary>Registered redirect URIs, matched exactly (see <see cref="RedirectUris.Matches"/>).</summary>
    public List<string> RedirectUris { get; set; } = [];

    /// <summary>Registered post-logout redirect URIs (used by RP-initiated logout).</summary>
    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>
    /// Where logout tokens are posted (OIDC Back-Channel Logout 1.0 A2.2); see
    /// <see cref="RedirectUris.IsValidBackChannelLogoutUri"/>. Without it the client isn't notified of logouts.
    /// </summary>
    public string? BackChannelLogoutUri { get; set; }

    /// <summary>Browser origins allowed to call the token, userinfo, and revocation endpoints (CORS).</summary>
    public List<string> AllowedCorsOrigins { get; set; } = [];

    /// <summary>Whether authorization requests must use PKCE. Always enforced for public clients.</summary>
    public bool RequirePkce { get; set; } = true;

    /// <summary>Overrides the global access token lifetime for this client.</summary>
    public TimeSpan? AccessTokenLifetime { get; set; }

    /// <summary>Overrides the global ID token lifetime for this client.</summary>
    public TimeSpan? IdTokenLifetime { get; set; }

    /// <summary>Overrides the global sliding refresh token lifetime for this client.</summary>
    public TimeSpan? RefreshTokenSlidingLifetime { get; set; }

    /// <summary>Overrides the global absolute refresh token lifetime for this client.</summary>
    public TimeSpan? RefreshTokenAbsoluteLifetime { get; set; }

    public bool PkceRequired => RequirePkce || Type == ClientType.Public;

    public required DateTimeOffset CreatedAt { get; init; }

    public List<ClientSecret> Secrets { get; init; } = [];
}

public sealed class ClientSecret
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ClientId { get; init; }

    /// <summary>SHA-256 hash of the secret. The plaintext is never stored.</summary>
    public required byte[] Hash { get; init; }

    public string? Description { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;
}
