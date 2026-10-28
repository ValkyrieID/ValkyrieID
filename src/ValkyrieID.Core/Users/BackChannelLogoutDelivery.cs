namespace ValkyrieID.Core.Users;

/// <summary>
/// A pending back-channel logout notification for one client. A fresh logout token is created for every attempt,
/// so no token is stored. The row is deleted once delivered or abandoned; the audit log keeps the outcome.
/// </summary>
public sealed class BackChannelLogoutDelivery
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The OAuth <c>client_id</c> to notify.</summary>
    public required string ClientId { get; init; }

    /// <summary>The user (<c>sub</c>).</summary>
    public required Guid UserId { get; init; }

    /// <summary>The ended session (<c>sid</c>).</summary>
    public required string SessionId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Attempts made so far.</summary>
    public int Attempts { get; set; }

    public required DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>The instance currently delivering this notification; it holds the row until <see cref="LeaseExpiresAt"/>.</summary>
    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
