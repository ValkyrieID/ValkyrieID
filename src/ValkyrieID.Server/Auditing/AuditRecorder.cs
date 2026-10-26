using System.Text.Json;
using ValkyrieID.Core.Auditing;
using ValkyrieID.Data;

namespace ValkyrieID.Server.Auditing;

/// <summary>Adds audit log entries to the current <see cref="ValkyrieDbContext"/>; they are saved with it.</summary>
internal sealed class AuditRecorder(ValkyrieDbContext db, TimeProvider timeProvider)
{
    public const string SystemActor = "system";
    public const string CliActor = "cli";

    public void Add(
        string eventType,
        bool success = true,
        string? actor = null,
        string? clientId = null,
        string? ipAddress = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = timeProvider.GetUtcNow(),
            EventType = eventType,
            Success = success,
            Actor = Truncate(actor, 200),
            ClientId = Truncate(clientId, 200),
            IpAddress = ipAddress,
            Details = details is null ? null : JsonSerializer.Serialize(details),
        });

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
