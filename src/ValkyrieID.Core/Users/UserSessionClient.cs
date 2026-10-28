namespace ValkyrieID.Core.Users;

/// <summary>
/// A client that received an authorization code from a session, so it is notified when the session ends (OIDC
/// Back-Channel Logout 1.0 A2.3). Duplicates are allowed, so concurrent sign-ins never conflict.
/// </summary>
public sealed class UserSessionClient
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string SessionId { get; init; }

    /// <summary>The OAuth <c>client_id</c>.</summary>
    public required string ClientId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
