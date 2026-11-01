namespace ValkyrieID.Core.Users;

public enum EmailTokenPurpose
{
    VerifyEmail = 1,
    ResetPassword = 2,
    ChangeEmail = 3,
}

/// <summary>
/// A single-use token sent by email (verification, password reset, email change). Only its SHA-256 hash is stored.
/// It's bound to the user's security stamp when issued, so a credential change voids it.
/// </summary>
public sealed class EmailToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required EmailTokenPurpose Purpose { get; init; }

    public required byte[] TokenHash { get; init; }

    /// <summary>The address the token was sent to: the address to verify, or the new address of an email change.</summary>
    public string? Email { get; init; }

    public required string SecurityStamp { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>
/// An email waiting to be sent. The recipient, subject, and body are encrypted with the master key, since they can
/// contain one-time links. The row is deleted once sent or abandoned.
/// </summary>
public sealed class OutboundEmail
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>What the email is for (e.g. <c>password_reset</c>), for the audit log.</summary>
    public required string Kind { get; init; }

    public Guid? UserId { get; init; }

    public required byte[] EncryptedMessage { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int Attempts { get; set; }

    public required DateTimeOffset NextAttemptAt { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
