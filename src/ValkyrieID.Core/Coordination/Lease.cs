namespace ValkyrieID.Core.Coordination;

/// <summary>
/// A named lease that lets one process at a time do a job shared by all instances, such as signing key rotation. A
/// holder that crashes loses it when it expires.
/// </summary>
public sealed class Lease
{
    public const string SigningKeys = "signing-keys";

    public required string Name { get; init; }

    public string? Owner { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
}
