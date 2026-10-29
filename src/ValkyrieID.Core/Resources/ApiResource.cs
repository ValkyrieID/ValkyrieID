namespace ValkyrieID.Core.Resources;

public sealed class ApiResource
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Unique name, used as the access token audience (<c>aud</c>).</summary>
    public required string Name { get; init; }

    public string? DisplayName { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the resource may send its secret in the request body when calling the introspection endpoint. Off
    /// unless enabled, like <c>client_secret_post</c> for clients (RFC 6749 §2.3.1).
    /// </summary>
    public bool AllowClientSecretPost { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<ApiScope> Scopes { get; init; } = [];

    /// <summary>Secrets that let the resource call the introspection endpoint.</summary>
    public List<ApiResourceSecret> Secrets { get; init; } = [];
}

public sealed class ApiScope
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ApiResourceId { get; init; }

    /// <summary>Scope name, unique across all API resources.</summary>
    public required string Name { get; init; }

    public string? Description { get; set; }
}

public sealed class ApiResourceSecret
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ApiResourceId { get; init; }

    /// <summary>SHA-256 hash of the secret. The plaintext is never stored.</summary>
    public required byte[] Hash { get; init; }

    public string? Description { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;
}
