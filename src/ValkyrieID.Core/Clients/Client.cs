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

    /// <summary>Overrides the global access token lifetime for this client.</summary>
    public TimeSpan? AccessTokenLifetime { get; set; }

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
