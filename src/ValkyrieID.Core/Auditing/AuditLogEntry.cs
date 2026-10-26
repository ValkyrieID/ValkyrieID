namespace ValkyrieID.Core.Auditing;

public sealed class AuditLogEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Event type, e.g. <c>login.succeeded</c> or <c>client.updated</c>.</summary>
    public required string EventType { get; init; }

    public bool Success { get; init; } = true;

    /// <summary>The user (<c>sub</c>) the event concerns, if any.</summary>
    public Guid? SubjectId { get; init; }

    /// <summary>Who performed the action: a user <c>sub</c>, a client id, or <c>cli</c> for maintenance commands.</summary>
    public string? Actor { get; init; }

    public string? ClientId { get; init; }

    public string? IpAddress { get; init; }

    /// <summary>Additional non-sensitive details as JSON. Never store secrets, tokens, or passwords here.</summary>
    public string? Details { get; init; }
}
